using System.Threading.Channels;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Core.Astro;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Slowly paints a virtual field of view with exposure time, Seestar-style: the scope keeps moving in small
/// stepovers, taking single shots, until every spot of the area has the target exposure. Frames can be heterogeneous
/// (sizes, mounting angles, offsets) and the whole scope can be turned between passes by a rotator.
/// <para>A pipeline of three tasks joined by channels, controlled by dRPC calls:</para>
/// <list type="bullet">
/// <item><b>producer</b>: the <see cref="CoveragePlanner"/> decides the next pose and queues it (bounded: it plans a few
/// shots ahead, never runs away, and counts queued shots as already painted);</item>
/// <item><b>executor</b>: turns the rotator if needed, has the smart scope slew there and take one shot;</item>
/// <item><b>recorder</b>: paints finished shots into the real coverage map and publishes the state.</item>
/// </list>
/// <c>SetTarget</c> and <c>SetStepover</c> change a running scan; the producer picks them up before its next decision.</summary>
public sealed class MosaicService : IAsyncDisposable
{
    /// <summary>How many visits the producer may queue ahead of the executor.</summary>
    public int Lookahead { get; set; } = 3;
    /// <summary>The deepest the visit queue has ever been (for tests and tuning).</summary>
    public int MaxQueueDepth { get; private set; }
    /// <summary>Seconds assumed per shot for time estimates, on top of the exposure (slew, settle, download).</summary>
    public double OverheadSecondsPerShot { get; set; } = 4;

    private sealed record Visit(int Index, Pose Pose);
    private sealed record Outcome(Visit Visit, string? Error);

    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<MosaicState> _publisher;
    private readonly object _gate = new();
    private MosaicState _state = new();
    private CoverageMap? _actual;
    private double _target, _stepover, _hop = double.NaN;
    private int _pass, _passes;
    private bool _retarget, _paused;
    private CancellationTokenSource? _cts;
    private Task? _run;
    private TaskCompletionSource _wake = NewWake();
    private ChannelReader<Visit>? _visitQueue;

    public MosaicService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, MosaicIds.State, MosaicIds.GetState, BuildState);
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync()
    {
        await _commands.AddAsync<MosaicRequest, MosaicPreview>(MosaicIds.Plan, r => Task.FromResult(Preview(r)), "validate and size a virtual-FOV painting without running it");
        await _commands.AddAsync<MosaicRequest, CommandResult>(MosaicIds.Start, StartRun, "paint a virtual field of view with exposure time, in small stepovers of single shots");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Pause, _ => Pause(true), "hold after the shot in progress");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Resume, _ => Pause(false), "carry on");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Abort, _ => Abort(), "stop now");
        await _commands.AddAsync<BinaryConvertibleDouble, CommandResult>(MosaicIds.SetTarget, SetTarget, "change the exposure every spot should receive, while running (0 = until aborted)");
        await _commands.AddAsync<BinaryConvertibleDouble, CommandResult>(MosaicIds.SetStepover, SetStepover, "change the largest hop between shots, while running");
        await _publisher.StartAsync();
    }

    // ---- request handling ---------------------------------------------------------------------------------------

    /// <summary>The numbers a request resolves to: the planner's inputs with defaults filled in.</summary>
    private sealed record Resolved(CoverageMap Map, FrameSpec[] Frames, double[] Rotations, double Stepover, double Cell, double ExtraMargin);

    private static string? Validate(MosaicRequest r)
    {
        if (!(r.FovWidthDegrees.Value > 0) || !(r.FovHeightDegrees.Value > 0)) return "the field of view must be larger than zero";
        if (r.Frames.Count == 0) return "describe at least one frame (the footprint of what the scope shoots)";
        foreach (var (f, i) in r.Frames.Select((f, i) => (f, i + 1)))
            if (!(f.WidthDegrees.Value > 0) || !(f.HeightDegrees.Value > 0)) return $"frame {i}: width and height must be larger than zero";
        if (r.Center.Epoch.Text != "J2000") return "the centre must be given in J2000";
        double ra = r.Center.RaHours.Value, dec = r.Center.DecDegrees.Value;
        if (double.IsNaN(ra) || ra < 0 || ra >= 24) return "the centre RA must be 0..24 hours";
        if (double.IsNaN(dec) || dec < -90 || dec > 90) return "the centre Dec must be -90..90 degrees";
        double reach = Math.Max(r.FovWidthDegrees.Value, r.FovHeightDegrees.Value) / 2 + r.Frames.Max(f => Math.Max(f.WidthDegrees.Value, f.HeightDegrees.Value));
        if (Math.Abs(dec) + reach > 89.5) return "the area reaches a celestial pole, where the tiling is undefined";
        if (!(r.Exposure.Seconds.Value > 0)) return "the exposure must be longer than zero (it is what gets painted)";
        if (r.TargetSeconds.Value < 0 || double.IsNaN(r.TargetSeconds.Value)) return "the target cannot be negative";
        if (r.StepoverDegrees.Value < 0 || r.CellDegrees.Value < 0 || r.MaxVisits.Value < 0) return "stepover, cell size and the visit limit cannot be negative";
        if (r.FieldRotations.Count > 0 && r.RotatorId.Text == "") return "field rotations need a rotator: set RotatorId";
        if (r.FieldRotations.Any(a => double.IsNaN(a.Value) || double.IsInfinity(a.Value))) return "a field rotation is not a number";
        return null;
    }

    private static Resolved Resolve(MosaicRequest r)
    {
        var frames = r.Frames.Select(f => new FrameSpec(f.WidthDegrees.Value / 2, f.HeightDegrees.Value / 2, f.RotationDegrees.Value, f.OffsetEastDegrees.Value, f.OffsetNorthDegrees.Value)).ToArray();
        double minFrame = frames.Min(f => 2 * Math.Min(f.HalfWidth, f.HalfHeight));
        double stepover = r.StepoverDegrees.Value > 0 ? r.StepoverDegrees.Value : 0.1 * minFrame;
        double cell = r.CellDegrees.Value > 0 ? r.CellDegrees.Value : minFrame / 12;
        cell = Math.Max(cell, Math.Sqrt(r.FovWidthDegrees.Value * r.FovHeightDegrees.Value / 40000.0));   // at most 40000 cells
        var map = new CoverageMap(r.FovWidthDegrees.Value, r.FovHeightDegrees.Value, cell);
        var rotations = r.FieldRotations.Select(a => a.Value).ToArray();
        return new Resolved(map, frames, rotations, stepover, cell, 0);
    }

    private MosaicPreview Preview(MosaicRequest r)
    {
        var p = new MosaicPreview();
        string? error = Validate(r);
        if (error is not null) { p.Error = error; return p; }
        var res = Resolve(r);
        var planner = new CoveragePlanner(res.Map, res.Frames, res.Rotations, r.PositionAngleDegrees.Value, r.Exposure.Seconds.Value, r.TargetSeconds.Value, res.Stepover);
        double hop = double.IsNaN(planner.PassHop) ? res.Stepover : planner.PassHop;
        p.StepoverDegrees = hop; p.CellDegrees = res.Cell; p.MapCols = res.Map.Cols; p.MapRows = res.Map.Rows;
        if (r.TargetSeconds.Value > 0)
        {
            double margin = res.Frames.Max(f => Math.Sqrt(f.HalfWidth * f.HalfWidth + f.HalfHeight * f.HalfHeight) + Math.Sqrt(f.OffsetEast * f.OffsetEast + f.OffsetNorth * f.OffsetNorth));
            double poses = planner.PassCount * (res.Map.FovWidth + 2 * margin) * (res.Map.FovHeight + 2 * margin) / (hop * hop);
            int visits = (int)Math.Ceiling(poses * 0.85 * 1.03);                         // useless poses are skipped; the top-up adds a little
            if (r.MaxVisits.Value > 0) visits = Math.Min(visits, r.MaxVisits.Value);
            p.EstimatedVisits = visits;
            p.EstimatedHours = visits * (r.Exposure.Seconds.Value + OverheadSecondsPerShot) / 3600.0;
        }
        return p;
    }

    private async Task<CommandResult> StartRun(MosaicRequest request)
    {
        if (request.ScopeId.Text == "") return CommandResult.Fail("ScopeId is required");
        string? error = Validate(request);
        if (error is not null) return CommandResult.Fail(error);
        var resolved = Resolve(request);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null) return CommandResult.Fail("a mosaic is already running");
            cts = _cts = new CancellationTokenSource();
            _actual = resolved.Map.Clone();
            _target = request.TargetSeconds.Value; _stepover = resolved.Stepover; _hop = double.NaN; _pass = 0; _passes = 0; _retarget = false; _paused = false; MaxQueueDepth = 0;
            _state = new MosaicState { Phase = "Running", Label = request.Label.Text };
        }
        await _publisher.PublishAsync();
        _run = Task.Run(() => RunAsync(Copy(request), resolved, cts));
        return CommandResult.Success();
    }

    private Task<CommandResult> Pause(bool pause)
    {
        lock (_gate)
        {
            if (_cts is null) return Task.FromResult(CommandResult.Fail("no mosaic is running"));
            _paused = pause;
        }
        Wake();
        return Task.FromResult(CommandResult.Success());
    }

    private Task<CommandResult> Abort()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
        return Task.FromResult(CommandResult.Success());
    }

    private async Task<CommandResult> SetTarget(BinaryConvertibleDouble seconds)
    {
        if (seconds.Value < 0 || double.IsNaN(seconds.Value)) return CommandResult.Fail("the target cannot be negative");
        lock (_gate)
        {
            if (_cts is null) return CommandResult.Fail("no mosaic is running");
            _target = seconds.Value; _retarget = true;
        }
        Wake();
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task<CommandResult> SetStepover(BinaryConvertibleDouble degrees)
    {
        if (!(degrees.Value > 0)) return CommandResult.Fail("the stepover must be larger than zero");
        lock (_gate)
        {
            if (_cts is null) return CommandResult.Fail("no mosaic is running");
            _stepover = degrees.Value; _retarget = true;
        }
        Wake();
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private void Wake() { TaskCompletionSource old; lock (_gate) { old = _wake; _wake = NewWake(); } old.TrySetResult(); }
    private Task WakeTask() { lock (_gate) return _wake.Task; }
    private bool IsPaused() { lock (_gate) return _paused; }

    // ---- the pipeline -------------------------------------------------------------------------------------------

    private async Task RunAsync(MosaicRequest req, Resolved res, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string scope = req.ScopeId.Text;
        var visits = Channel.CreateBounded<Visit>(new BoundedChannelOptions(Math.Max(1, Lookahead)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var outcomes = Channel.CreateUnbounded<Outcome>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _visitQueue = visits.Reader;
        using var scopeState = new RemoteState<ScopeState>(_node, ScopeIds.State(scope), ScopeIds.GetState(scope));
        RemoteState<RotatorState>? rotator = req.RotatorId.Text == "" ? null
            : new RemoteState<RotatorState>(_node, EquipmentIds.State(DeviceKinds.Rotator, req.RotatorId.Text), EquipmentIds.GetState(DeviceKinds.Rotator, req.RotatorId.Text));
        RemoteState<WeatherState>? weather = req.WeatherId.Text == "" ? null
            : new RemoteState<WeatherState>(_node, EquipmentIds.State(DeviceKinds.Weather, req.WeatherId.Text), EquipmentIds.GetState(DeviceKinds.Weather, req.WeatherId.Text));
        string endPhase = "Done", endMessage = "";
        try
        {
            await scopeState.StartAsync();
            if (scopeState.Latest is null) throw new InvalidOperationException($"scope {scope} not found");
            if (rotator is not null)
            {
                await rotator.StartAsync();
                if (rotator.Latest is not { Connected.Value: true }) throw new InvalidOperationException($"rotator {req.RotatorId.Text} is not connected");
            }
            if (weather is not null) { await weather.StartAsync(); weather.Changed += _ => Wake(); }

            // The stages share one cancellation: a failing stage must stop the others, or a producer blocked on a
            // full queue would wait for ever.
            using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Exception? failure = null;
            async Task Stage(Func<CancellationToken, Task> body)
            {
                try { await body(pipeline.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); pipeline.Cancel(); }
            }
            await Task.WhenAll(
                Task.Run(() => Stage(t => ProduceAsync(req, res, visits.Writer, t))),
                Task.Run(() => Stage(t => ExecuteAsync(req, res, scope, scopeState, rotator, weather, visits.Reader, outcomes.Writer, t))),
                Task.Run(() => Stage(t => RecordAsync(req, res, outcomes.Reader, t))));
            if (failure is not null) throw failure;
            ct.ThrowIfCancellationRequested();     // the user aborted
        }
        catch (OperationCanceledException)
        {
            endPhase = "Aborted"; endMessage = "aborted";
            await Commands.CallAsync(_node, ScopeIds.Command(scope, "Abort"), NOTESVoid.Void);
        }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            weather?.Dispose(); rotator?.Dispose();
            lock (_gate) { _cts = null; _paused = false; _visitQueue = null; }
            cts.Dispose();
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; s.QueueDepth = 0; });
        }
    }

    /// <summary>Producer: the planner decides, the queue paces it. Live settings are applied before each decision.</summary>
    private async Task ProduceAsync(MosaicRequest req, Resolved res, ChannelWriter<Visit> queue, CancellationToken ct)
    {
        try
        {
            var planner = new CoveragePlanner(res.Map, res.Frames, res.Rotations, req.PositionAngleDegrees.Value, req.Exposure.Seconds.Value, _target, res.Stepover);
            int limit = req.MaxVisits.Value;
            for (int index = 1; limit == 0 || index <= limit; index++)
            {
                ct.ThrowIfCancellationRequested();
                bool replan; double target, stepover;
                lock (_gate) { replan = _retarget; _retarget = false; target = _target; stepover = _stepover; }
                if (replan) { planner.TargetSeconds = target; planner.Stepover = stepover; planner.Plan(); }
                if (planner.Next() is not { } pose) break;                   // target reached (or nothing left to improve)
                double hop = double.IsNaN(planner.PassHopX) ? planner.PassHop : Math.Max(planner.PassHopX, planner.PassHopY);
                int pass = planner.InPasses ? planner.CurrentPass : 0, passes = planner.PassCount;
                lock (_gate) { _hop = hop; _pass = pass; _passes = passes; }
                await queue.WriteAsync(new Visit(index, pose), ct);
                NoteDepth();
            }
        }
        finally { queue.TryComplete(); }
    }

    private void NoteDepth()
    {
        int depth = _visitQueue?.Count ?? 0;
        if (depth > MaxQueueDepth) MaxQueueDepth = depth;
    }

    /// <summary>Executor: one visit = turn the rotator if the field angle changed, slew there and take one shot, through the scope.</summary>
    private async Task ExecuteAsync(MosaicRequest req, Resolved res, string scope, RemoteState<ScopeState> scopeState, RemoteState<RotatorState>? rotator,
        RemoteState<WeatherState>? weather, ChannelReader<Visit> queue, ChannelWriter<Outcome> outcomes, CancellationToken ct)
    {
        double? rotatorAt = null;
        try
        {
            await foreach (var visit in queue.ReadAllAsync(ct))
            {
                await WaitUntilAllowed(weather, ct);
                await Set(s => { s.PoseX = visit.Pose.X; s.PoseY = visit.Pose.Y; s.PoseFieldAngle = visit.Pose.FieldAngle; s.QueueDepth = _visitQueue?.Count ?? 0; if (s.Phase.Text != "Running") s.Phase = "Running"; s.Message = ""; });
                string error = "";
                if (rotator is not null && (rotatorAt is null || Math.Abs(rotatorAt.Value - visit.Pose.FieldAngle) > 1e-6))
                {
                    error = await TurnRotatorAsync(req, rotator, visit.Pose.FieldAngle, ct);
                    if (error == "") rotatorAt = visit.Pose.FieldAngle;
                }
                if (error == "") error = await ShootAsync(req, scope, scopeState, visit, ct);
                await outcomes.WriteAsync(new Outcome(visit, error == "" ? null : error), ct);
                if (error != "") throw new InvalidOperationException(error);
            }
        }
        finally { outcomes.TryComplete(); }
    }

    private async Task<string> TurnRotatorAsync(MosaicRequest req, RemoteState<RotatorState> rotator, double fieldAngle, CancellationToken ct)
    {
        double angle = (((fieldAngle - req.RotatorOffsetDegrees.Value) % 360) + 360) % 360;
        var r = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Rotator, req.RotatorId.Text, "MoveTo"), (BinaryConvertibleDouble)angle);
        if (!r.Ok.Value) return r.Error.Text;
        try
        {
            await rotator.WaitAsync(s => !s.Moving.Value && Math.Abs(((s.AngleDegrees.Value - angle + 540) % 360) - 180) < 0.5, TimeSpan.FromMinutes(3), ct);
        }
        catch (TimeoutException) { return "the rotator did not arrive in time"; }
        return "";
    }

    private async Task<string> ShootAsync(MosaicRequest req, string scope, RemoteState<ScopeState> scopeState, Visit visit, CancellationToken ct)
    {
        var (east, north) = ToSkyOffsets(visit.Pose.X, visit.Pose.Y, req.PositionAngleDegrees.Value);
        var (ra, dec) = Gnomonic.ToSky(req.Center.RaHours.Value, req.Center.DecDegrees.Value, east, north);
        var start = await Commands.CallAsync(_node, ScopeIds.Command(scope, "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            Exposure = req.Exposure, Count = 1, SlewTimeoutSeconds = req.SlewTimeoutSeconds.Value,
            ObjectName = req.Label.Text, PlanId = req.Label.Text,
        });
        if (!start.Ok.Value) return start.Error.Text;
        var end = await scopeState.WaitAsync(s => !s.Observing.Value, TimeSpan.FromHours(6), ct);
        if (end.Phase.Text == "Error") return end.Message.Text != "" ? end.Message.Text : "the scope reported an error";
        return end.ShotsDone.Value >= 1 ? "" : "the shot was not taken";
    }

    /// <summary>The area's own axes (x along its width, y along its height) to sky east/north offsets.</summary>
    public static (double East, double North) ToSkyOffsets(double x, double y, double positionAngleDegrees)
    {
        double pa = positionAngleDegrees * Math.PI / 180;
        return (x * Math.Cos(pa) + y * Math.Sin(pa), -x * Math.Sin(pa) + y * Math.Cos(pa));
    }

    /// <summary>Recorder: paints finished shots into the real coverage map and publishes it.</summary>
    private async Task RecordAsync(MosaicRequest req, Resolved res, ChannelReader<Outcome> results, CancellationToken ct)
    {
        await foreach (var outcome in results.ReadAllAsync(ct))
        {
            if (outcome.Error is not null) continue;
            var footprints = CoverageMap.Footprints(outcome.Visit.Pose, res.Frames, req.PositionAngleDegrees.Value);
            lock (_gate) _actual!.Paint(footprints, req.Exposure.Seconds.Value);
            await Set(s => s.Visits = s.Visits.Value + 1);
        }
    }

    private async Task WaitUntilAllowed(RemoteState<WeatherState>? weather, CancellationToken ct)
    {
        while (IsPaused() || weather?.Latest is { Safety.Text: "Unsafe" })
        {
            string phase = IsPaused() ? "Paused" : "WaitingForWeather";
            await Set(s => { s.Phase = phase; s.Message = phase == "Paused" ? "paused" : "weather is unsafe"; });
            var wake = WakeTask();
            if (!IsPaused() && weather?.Latest is not { Safety.Text: "Unsafe" }) break;
            await wake.WaitAsync(ct);
        }
    }

    // ---- state --------------------------------------------------------------------------------------------------

    private async Task Set(Action<MosaicState> change)
    {
        lock (_gate) change(_state);
        await _publisher.PublishAsync();
    }

    private MosaicState BuildState()
    {
        lock (_gate)
        {
            var s = new MosaicState
            {
                Phase = _state.Phase.Text, Label = _state.Label.Text, Message = _state.Message.Text, Visits = _state.Visits.Value,
                QueueDepth = _state.QueueDepth.Value, TargetSeconds = _target, StepoverDegrees = _stepover, PassHopDegrees = _hop, Pass = _pass, Passes = _passes,
                PoseX = _state.PoseX.Value, PoseY = _state.PoseY.Value, PoseFieldAngle = _state.PoseFieldAngle.Value,
            };
            if (_actual is { } map)
            {
                s.MinSeconds = map.Min(); s.MeanSeconds = map.Mean(); s.MaxSeconds = map.Max();
                var bytes = map.Render(Math.Max(_target, map.Max()), 96, 64, out int cols, out int rows);
                s.MapCols = cols; s.MapRows = rows; s.Map = new RawBytes(bytes);
            }
            return s;
        }
    }

    private static MosaicRequest Copy(MosaicRequest r)
    {
        var c = new MosaicRequest
        {
            Label = r.Label.Text, ScopeId = r.ScopeId.Text, WeatherId = r.WeatherId.Text, RotatorId = r.RotatorId.Text, RotatorOffsetDegrees = r.RotatorOffsetDegrees.Value,
            Center = new SkyTarget { RaHours = r.Center.RaHours.Value, DecDegrees = r.Center.DecDegrees.Value, Epoch = r.Center.Epoch.Text },
            FovWidthDegrees = r.FovWidthDegrees.Value, FovHeightDegrees = r.FovHeightDegrees.Value, PositionAngleDegrees = r.PositionAngleDegrees.Value,
            StepoverDegrees = r.StepoverDegrees.Value, TargetSeconds = r.TargetSeconds.Value, MaxVisits = r.MaxVisits.Value, CellDegrees = r.CellDegrees.Value,
            SlewTimeoutSeconds = r.SlewTimeoutSeconds.Value,
            Exposure = new ShooterExposure
            {
                Seconds = r.Exposure.Seconds.Value, FrameType = r.Exposure.FrameType.Text, Filter = r.Exposure.Filter.Text,
                BinX = r.Exposure.BinX.Value, BinY = r.Exposure.BinY.Value, Gain = r.Exposure.Gain.Value,
            },
        };
        foreach (var f in r.Frames)
            c.Frames.Add(new MosaicFrame
            {
                Label = f.Label.Text, WidthDegrees = f.WidthDegrees.Value, HeightDegrees = f.HeightDegrees.Value, RotationDegrees = f.RotationDegrees.Value,
                OffsetEastDegrees = f.OffsetEastDegrees.Value, OffsetNorthDegrees = f.OffsetNorthDegrees.Value,
            });
        foreach (var a in r.FieldRotations) c.FieldRotations.Add(a.Value);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
