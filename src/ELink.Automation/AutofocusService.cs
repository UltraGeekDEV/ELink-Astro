using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Autofocus as a service on the mesh: it knows a Shooter and a Focuser only by id. It sweeps the focuser
/// through a window of positions, measures the half-flux radius of the stars in a frame at each, fits the focus curve
/// and moves to its vertex; if the vertex lies outside the window it re-centres and sweeps again.</summary>
public sealed class AutofocusService : IAsyncDisposable
{
    private const int MaxRounds = 4;

    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<AutofocusState> _publisher;
    private readonly object _gate = new();
    private AutofocusState _state = new();
    private CancellationTokenSource? _cts;
    private Task? _run;

    public AutofocusService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, AutofocusIds.State, AutofocusIds.GetState, () => { lock (_gate) return Clone(_state); });
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<AutofocusRequest, CommandResult>(AutofocusIds.Run, StartRun, "focus a shooter by sweeping a focuser and measuring star sharpness");
        await _commands.AddAsync<NOTESVoid, CommandResult>(AutofocusIds.Abort, _ => Abort(), "abort a running autofocus");
        await _publisher.StartAsync();
    }

    private Task<CommandResult> StartRun(AutofocusRequest r)
    {
        if (r.ShooterId.Text == "" || r.FocuserId.Text == "") return Task.FromResult(CommandResult.Fail("ShooterId and FocuserId are required"));
        if (r.StepSize.Value < 1) return Task.FromResult(CommandResult.Fail("StepSize must be positive"));
        if (!(r.ExposureSeconds.Value > 0)) return Task.FromResult(CommandResult.Fail("ExposureSeconds must be positive"));
        lock (_gate)
        {
            if (_cts is not null) return Task.FromResult(CommandResult.Fail("autofocus is already running"));
            _cts = new CancellationTokenSource();
            _state = new AutofocusState { Phase = "Moving" };
        }
        var cts = _cts;
        _run = Task.Run(() => RunAsync(r, cts!));
        return Task.FromResult(CommandResult.Success());
    }

    private Task<CommandResult> Abort()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        return Task.FromResult(CommandResult.Success());
    }

    private async Task Set(Action<AutofocusState> change)
    {
        lock (_gate) change(_state);
        await _publisher.PublishAsync();
    }

    private async Task RunAsync(AutofocusRequest req, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string shooter = req.ShooterId.Text, focuser = req.FocuserId.Text;
        var shots = Channel.CreateUnbounded<ShotEvent>();
        Action<ShotEvent> onShot = s => shots.Writer.TryWrite(s);
        using var focuserState = new RemoteState<FocuserState>(_node, EquipmentIds.State(DeviceKinds.Focuser, focuser), EquipmentIds.GetState(DeviceKinds.Focuser, focuser));
        string endPhase = "Error", endMessage = "";
        try
        {
            await focuserState.StartAsync();
            await _node.HookEventAsync(ShooterIds.Shot(shooter), onShot, "autofocus frames");
            var f0 = focuserState.Latest ?? throw new InvalidOperationException($"focuser {focuser} not found");
            if (!f0.Connected.Value) throw new InvalidOperationException($"focuser {focuser} is not connected");
            if (!f0.CanMoveAbsolute.Value) throw new InvalidOperationException($"focuser {focuser} cannot move to absolute positions");
            int max = f0.MaxPosition.Value > 0 ? f0.MaxPosition.Value : int.MaxValue;
            int step = req.StepSize.Value, n = Math.Clamp(req.Samples.Value, 5, 15);
            int center = f0.Position.Value;

            // coarse sweeps until the vertex of the fitted curve is well inside the sampled window
            double? bestPos = null; double bestHfr = double.NaN;
            for (int round = 1; round <= MaxRounds && bestPos is null; round++)
            {
                await Set(s => { s.Round = round; s.Phase = "Moving"; s.Message = ""; });
                var (sweep, fit) = await Sweep(center, step, n, max, req, focuser, shooter, focuserState, shots.Reader, ct);
                if (sweep.Count(p => !double.IsNaN(p.Hfr.Value)) < 3) throw new InvalidOperationException("not enough stars to measure focus (check exposure, pointing and the starting focus)");
                if (!fit.Valid)
                {
                    // no minimum in view: head for the sharpest sample and sweep around it
                    center = sweep.Where(p => !double.IsNaN(p.Hfr.Value)).MinBy(p => p.Hfr.Value)!.Position.Value;
                    continue;
                }
                if (Bracketed(sweep, fit.BestPosition)) { bestPos = fit.BestPosition; bestHfr = fit.BestHfr; }
                else center = (int)Math.Round(Math.Clamp(fit.BestPosition, 0, max));      // vertex outside or at the edge: re-centre
            }
            if (bestPos is null) throw new InvalidOperationException("autofocus did not converge");

            // a finer sweep around the coarse result
            int fineStep = Math.Max(1, step / 3);
            await Set(s => { s.Round = s.Round.Value + 1; s.Message = "refining"; });
            var (fineSweep, fineFit) = await Sweep((int)Math.Round(bestPos.Value), fineStep, Math.Min(n, 7), max, req, focuser, shooter, focuserState, shots.Reader, ct);
            if (fineFit.Valid && Bracketed(fineSweep, fineFit.BestPosition)) { bestPos = fineFit.BestPosition; bestHfr = fineFit.BestHfr; }

            int best = (int)Math.Round(Math.Clamp(bestPos.Value, 0, max));
            await Set(s => { s.Phase = "Moving"; s.Message = ""; });
            var check = await Measure(best, req, focuser, shooter, focuserState, shots.Reader, ct);
            await Set(s => { s.Points.Add(check); s.BestPosition = best; s.BestHfr = double.IsNaN(check.Hfr.Value) ? bestHfr : check.Hfr.Value; });
            endPhase = "Done";
            return;
        }
        catch (OperationCanceledException)
        {
            endPhase = "Aborted"; endMessage = "aborted";
            await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, focuser, "Abort"), NOTESVoid.Void);
            await Commands.CallAsync(_node, ShooterIds.Abort(shooter), NOTESVoid.Void);
        }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            try { _node.UnhookEvent(ShooterIds.Shot(shooter), onShot); } catch (ObjectDisposedException) { }
            lock (_gate) _cts = null;
            cts.Dispose();
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; });
        }
    }

    /// <summary>The vertex has measured samples on both sides, at least two each: otherwise it is an extrapolation.</summary>
    private static bool Bracketed(IReadOnlyList<FocusPoint> sweep, double vertex)
    {
        var measured = sweep.Where(p => !double.IsNaN(p.Hfr.Value)).ToList();
        return measured.Count(p => p.Position.Value < vertex) >= 2 && measured.Count(p => p.Position.Value > vertex) >= 2;
    }

    private async Task<(List<FocusPoint> Points, FocusCurve.Fit Fit)> Sweep(int center, int step, int n, int max, AutofocusRequest req, string focuser, string shooter,
        RemoteState<FocuserState> state, ChannelReader<ShotEvent> shots, CancellationToken ct)
    {
        var positions = Enumerable.Range(0, n).Select(i => Math.Clamp(center + (i - (n - 1) / 2) * step, 0, max)).Distinct().Order().ToList();
        if (positions.Count < 4) throw new InvalidOperationException("the focuser range is too small for a sweep");
        var points = new List<FocusPoint>();
        foreach (int pos in positions)        // always in the same direction, which keeps backlash out of the picture
        {
            var point = await Measure(pos, req, focuser, shooter, state, shots, ct);
            points.Add(point);
            await Set(s => s.Points.Add(point));
        }
        await Set(s => s.Phase = "Fitting");
        return (points, FocusCurve.Parabola(points.Select(p => ((double)p.Position.Value, p.Hfr.Value)).ToList()));
    }

    /// <summary>Move to a position, take a frame there, measure it.</summary>
    private async Task<FocusPoint> Measure(int position, AutofocusRequest req, string focuser, string shooter,
        RemoteState<FocuserState> state, ChannelReader<ShotEvent> shots, CancellationToken ct)
    {
        await Set(s => s.Phase = "Moving");
        var move = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, focuser, "MoveTo"), (BinaryConvertibleInt32)position);
        if (!move.Ok.Value) throw new InvalidOperationException(move.Error.Text);
        await state.WaitAsync(s => s.Position.Value == position && !s.Moving.Value, TimeSpan.FromSeconds(180), ct);

        while (shots.TryRead(out _)) { }                       // forget frames from before the move
        await Set(s => s.Phase = "Exposing");
        var expose = await Commands.CallAsync(_node, ShooterIds.Expose(shooter),
            new ShooterExposure { Seconds = req.ExposureSeconds.Value, FrameType = "Light", Filter = req.Filter.Text });
        if (!expose.Ok.Value) throw new InvalidOperationException(expose.Error.Text);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(req.ExposureSeconds.Value + 90));
        ShotEvent shot;
        try { shot = await shots.ReadAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("the frame did not arrive in time"); }

        var point = new FocusPoint { Position = position };
        if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"autofocus needs FITS frames, the shooter delivered {shot.Format.Text}");
        var result = await Task.Run(() => StarField.Detect(FitsImage.Parse(shot.Data.Data)), ct);
        point.Hfr = result.MedianHfr; point.Stars = result.Count;
        return point;
    }

    private static AutofocusState Clone(AutofocusState s)
    {
        var c = new AutofocusState { Phase = s.Phase.Text, Message = s.Message.Text, Round = s.Round.Value, BestPosition = s.BestPosition.Value, BestHfr = s.BestHfr.Value };
        foreach (var p in s.Points) c.Points.Add(new FocusPoint { Position = p.Position.Value, Hfr = p.Hfr.Value, Stars = p.Stars.Value });
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
