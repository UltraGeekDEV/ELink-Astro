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
/// and moves to its vertex; if the vertex lies outside the window it re-centres and sweeps again. Runs on different
/// focusers go on at the same time (several scopes refocusing each their own train); the state shows the latest.</summary>
public sealed class AutofocusService : IAsyncDisposable
{
    private const int MaxRounds = 4;

    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<AutofocusState> _publisher;
    private readonly object _gate = new();
    private sealed class RunCtx
    {
        public AutofocusState State = new();
        public CancellationTokenSource Cts = new();
        public Task? Task;
        public readonly TaskCompletionSource<AutofocusState> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, RunCtx> _runs = new();   // by focuser
    private RunCtx _latest = new();

    public AutofocusService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, AutofocusIds.State, AutofocusIds.GetState, () => { lock (_gate) return Clone(_latest.State); });
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<AutofocusRequest, CommandResult>(AutofocusIds.Run, async r => (await StartRun(r)).Result, "focus a shooter by sweeping a focuser and measuring star sharpness");
        await _commands.AddAsync<AutofocusRequest, AutofocusState>(AutofocusIds.RunAndWait, async r =>
        {
            var (result, ctx) = await StartRun(r);
            if (ctx is null) return new AutofocusState { Phase = "Error", Message = result.Error.Text, FocuserId = r.FocuserId.Text };
            return await ctx.Done.Task;
        }, "focus and answer when done, with the result");
        await _commands.AddAsync<NOTESVoid, CommandResult>(AutofocusIds.Abort, _ => Abort(), "abort a running autofocus");
        await _publisher.StartAsync();
    }

    private async Task<(CommandResult Result, RunCtx? Ctx)> StartRun(AutofocusRequest r)
    {
        if (r.ShooterId.Text == "" || r.FocuserId.Text == "") return (CommandResult.Fail("ShooterId and FocuserId are required"), null);
        if (r.StepSize.Value < 1) return (CommandResult.Fail("StepSize must be positive"), null);
        if (!(r.ExposureSeconds.Value > 0)) return (CommandResult.Fail("ExposureSeconds must be positive"), null);
        var ctx = new RunCtx();
        lock (_gate)
        {
            if (_runs.ContainsKey(r.FocuserId.Text)) return (CommandResult.Fail($"autofocus is already running on {r.FocuserId.Text}"), null);
            _runs[r.FocuserId.Text] = ctx;
            ctx.State = new AutofocusState { Phase = "Moving", FocuserId = r.FocuserId.Text, ShooterId = r.ShooterId.Text };
            _latest = ctx;
        }
        // Publish the new run before answering: a caller that then waits for "Done" must not see the previous run's result.
        await _publisher.PublishAsync();
        ctx.Task = Task.Run(() => RunAsync(r, ctx));
        return (CommandResult.Success(), ctx);
    }

    private Task<CommandResult> Abort()
    {
        List<RunCtx> runs;
        lock (_gate) runs = _runs.Values.ToList();
        foreach (var r in runs) { try { r.Cts.Cancel(); } catch (ObjectDisposedException) { } }
        return Task.FromResult(CommandResult.Success());
    }

    private async Task Set(RunCtx ctx, Action<AutofocusState> change)
    {
        lock (_gate) { change(ctx.State); _latest = ctx; }
        await _publisher.PublishAsync();
    }

    private async Task RunAsync(AutofocusRequest req, RunCtx ctx)
    {
        var cts = ctx.Cts;
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
                await Set(ctx, s => { s.Round = round; s.Phase = "Moving"; s.Message = ""; });
                var (sweep, fit) = await Sweep(ctx, center, step, n, max, req, focuser, shooter, focuserState, shots.Reader, ct);
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
            await Set(ctx, s => { s.Round = s.Round.Value + 1; s.Message = "refining"; });
            var (fineSweep, fineFit) = await Sweep(ctx, (int)Math.Round(bestPos.Value), fineStep, Math.Min(n, 7), max, req, focuser, shooter, focuserState, shots.Reader, ct);
            if (fineFit.Valid && Bracketed(fineSweep, fineFit.BestPosition)) { bestPos = fineFit.BestPosition; bestHfr = fineFit.BestHfr; }

            int best = (int)Math.Round(Math.Clamp(bestPos.Value, 0, max));
            await Set(ctx, s => { s.Phase = "Moving"; s.Message = ""; });
            var check = await Measure(ctx, best, req, focuser, shooter, focuserState, shots.Reader, ct);
            await Set(ctx, s => { s.Points.Add(check); s.BestPosition = best; s.BestHfr = double.IsNaN(check.Hfr.Value) ? bestHfr : check.Hfr.Value; });
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
            lock (_gate) _runs.Remove(focuser);
            cts.Dispose();
            await Set(ctx, s => { s.Phase = endPhase; s.Message = endMessage; });
            AutofocusState final; lock (_gate) final = Clone(ctx.State);
            ctx.Done.TrySetResult(final);
        }
    }

    /// <summary>The vertex has measured samples on both sides, at least two each: otherwise it is an extrapolation.</summary>
    private static bool Bracketed(IReadOnlyList<FocusPoint> sweep, double vertex)
    {
        var measured = sweep.Where(p => !double.IsNaN(p.Hfr.Value)).ToList();
        return measured.Count(p => p.Position.Value < vertex) >= 2 && measured.Count(p => p.Position.Value > vertex) >= 2;
    }

    private async Task<(List<FocusPoint> Points, FocusCurve.Fit Fit)> Sweep(RunCtx ctx, int center, int step, int n, int max, AutofocusRequest req, string focuser, string shooter,
        RemoteState<FocuserState> state, ChannelReader<ShotEvent> shots, CancellationToken ct)
    {
        var positions = Enumerable.Range(0, n).Select(i => Math.Clamp(center + (i - (n - 1) / 2) * step, 0, max)).Distinct().Order().ToList();
        if (positions.Count < 4) throw new InvalidOperationException("the focuser range is too small for a sweep");
        var points = new List<FocusPoint>();
        foreach (int pos in positions)        // always in the same direction, which keeps backlash out of the picture
        {
            var point = await Measure(ctx, pos, req, focuser, shooter, state, shots, ct);
            points.Add(point);
            await Set(ctx, s => s.Points.Add(point));
        }
        await Set(ctx, s => s.Phase = "Fitting");
        return (points, FocusCurve.Parabola(points.Select(p => ((double)p.Position.Value, p.Hfr.Value)).ToList()));
    }

    /// <summary>Move to a position, take a frame there, measure it.</summary>
    private async Task<FocusPoint> Measure(RunCtx ctx, int position, AutofocusRequest req, string focuser, string shooter,
        RemoteState<FocuserState> state, ChannelReader<ShotEvent> shots, CancellationToken ct)
    {
        await Set(ctx, s => s.Phase = "Moving");
        var move = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, focuser, "MoveTo"), (BinaryConvertibleInt32)position);
        if (!move.Ok.Value) throw new InvalidOperationException(move.Error.Text);
        await state.WaitAsync(s => s.Position.Value == position && !s.Moving.Value, TimeSpan.FromSeconds(180), ct);

        while (shots.TryRead(out _)) { }                       // forget frames from before the move
        await Set(ctx, s => s.Phase = "Exposing");
        var expose = await Commands.CallAsync(_node, ShooterIds.Expose(shooter),
            new ShooterExposure { Seconds = req.ExposureSeconds.Value, FrameType = "Light", Filter = req.Channel.Text != "" ? "Raw" : req.Filter.Text });
        if (!expose.Ok.Value) throw new InvalidOperationException(expose.Error.Text);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(req.ExposureSeconds.Value + 90));
        ShotEvent shot;
        try { shot = await shots.ReadAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("the frame did not arrive in time"); }

        var point = new FocusPoint { Position = position };
        if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"autofocus needs FITS frames, the shooter delivered {shot.Format.Text}");
        var image = FitsImage.Parse(shot.Data.Data);
        if (req.Channel.Text != "") image = FocusMeasure.ColourPlane(image, req.Channel.Text);
        var result = await Task.Run(() => StarField.Detect(image), ct);
        point.Hfr = result.MedianHfr; point.Stars = result.Count;
        return point;
    }

    private static AutofocusState Clone(AutofocusState s)
    {
        var c = new AutofocusState { Phase = s.Phase.Text, Message = s.Message.Text, Round = s.Round.Value, BestPosition = s.BestPosition.Value, BestHfr = s.BestHfr.Value, FocuserId = s.FocuserId.Text, ShooterId = s.ShooterId.Text };
        foreach (var p in s.Points) c.Points.Add(new FocusPoint { Position = p.Position.Value, Hfr = p.Hfr.Value, Stars = p.Stars.Value });
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        List<RunCtx> runs;
        lock (_gate) runs = _runs.Values.ToList();
        foreach (var r in runs) { try { r.Cts.Cancel(); } catch (ObjectDisposedException) { } }
        await Task.WhenAny(Task.WhenAll(runs.Select(r => r.Task ?? Task.CompletedTask)), Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
