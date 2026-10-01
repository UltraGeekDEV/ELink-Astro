using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Covers a virtual field of view with many single shots, one panel after another, round after round, the way
/// a Seestar mosaic scans: no deep stack per panel, the scope just keeps moving over the whole area.
/// <para>It is a three-stage pipeline of tasks joined by channels, controlled by dRPC calls:</para>
/// <list type="bullet">
/// <item><b>producer</b> walks the serpentine route, round after round, and queues visits into a bounded channel
/// (its lookahead: it can plan a few shots ahead but never run away from the executor);</item>
/// <item><b>executor</b> takes a visit, has the smart scope slew there and take one shot, and reports the outcome;</item>
/// <item><b>recorder</b> counts finished visits into the coverage map and publishes the state.</item>
/// </list>
/// The producer re-reads <c>SetPasses</c> and <c>SkipPanel</c> before every visit, so the plan can be changed while it
/// runs. The scope is only an id: anything that is a Pointer and a Shooter will do.</summary>
public sealed class MosaicService : IAsyncDisposable
{
    /// <summary>How many visits the producer may queue ahead of the executor.</summary>
    public int Lookahead { get; set; } = 3;
    /// <summary>The deepest the visit queue has ever been (for tests and tuning).</summary>
    public int MaxQueueDepth { get; private set; }

    private sealed record Visit(int Pass, int Row, int Col, double RaHours, double DecDegrees);
    private sealed record Outcome(Visit Visit, string? Error);

    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<MosaicState> _publisher;
    private readonly object _gate = new();
    private MosaicState _state = new();
    private MosaicLayout _layout = new();
    private readonly HashSet<(int, int)> _skipped = new();
    private readonly Dictionary<(int, int), int> _frames = new();
    private int _passes;
    private bool _paused;
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
        await _commands.AddAsync<MosaicRequest, MosaicLayout>(MosaicIds.Plan, r => Task.FromResult(MosaicGeometry.Plan(r)), "tile a virtual field of view into panels, without running anything");
        await _commands.AddAsync<MosaicRequest, CommandResult>(MosaicIds.Start, StartRun, "scan a virtual field of view with many single shots");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Pause, _ => Pause(true), "hold after the shot in progress");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Resume, _ => Pause(false), "carry on");
        await _commands.AddAsync<NOTESVoid, CommandResult>(MosaicIds.Abort, _ => Abort(), "stop now");
        await _commands.AddAsync<BinaryConvertibleInt32, CommandResult>(MosaicIds.SetPasses, SetPasses, "change the number of rounds while running (0 = until aborted)");
        await _commands.AddAsync<PanelSkip, CommandResult>(MosaicIds.SkipPanel, SkipPanel, "leave a panel out of (or back into) the scan while running");
        await _publisher.StartAsync();
    }

    // ---- control (dRPC) -----------------------------------------------------------------------------------------

    private async Task<CommandResult> StartRun(MosaicRequest request)
    {
        if (request.ScopeId.Text == "") return CommandResult.Fail("ScopeId is required");
        if (!(request.Exposure.Seconds.Value >= 0)) return CommandResult.Fail("invalid exposure time");
        var layout = MosaicGeometry.Plan(request);
        if (layout.Error.Text != "") return CommandResult.Fail(layout.Error.Text);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null) return CommandResult.Fail("a mosaic is already running");
            cts = _cts = new CancellationTokenSource();
            _layout = layout; _skipped.Clear(); _frames.Clear(); _paused = false; MaxQueueDepth = 0;
            _passes = request.Passes.Value;
            _state = new MosaicState { Phase = "Running", Label = request.Label.Text };
        }
        await _publisher.PublishAsync();
        _run = Task.Run(() => RunAsync(Copy(request), layout, cts));
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

    private async Task<CommandResult> SetPasses(BinaryConvertibleInt32 passes)
    {
        if (passes.Value < 0) return CommandResult.Fail("Passes cannot be negative");
        lock (_gate)
        {
            if (_cts is null) return CommandResult.Fail("no mosaic is running");
            _passes = passes.Value;
        }
        Wake();
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task<CommandResult> SkipPanel(PanelSkip skip)
    {
        lock (_gate)
        {
            if (_cts is null) return CommandResult.Fail("no mosaic is running");
            if (skip.Row.Value < 0 || skip.Row.Value >= _layout.Rows.Value || skip.Col.Value < 0 || skip.Col.Value >= _layout.Cols.Value)
                return CommandResult.Fail("no such panel");
            if (skip.Skip.Value) _skipped.Add((skip.Row.Value, skip.Col.Value)); else _skipped.Remove((skip.Row.Value, skip.Col.Value));
            if (_skipped.Count >= _layout.Rows.Value * _layout.Cols.Value) { _skipped.Remove((skip.Row.Value, skip.Col.Value)); return CommandResult.Fail("at least one panel must stay in the scan"); }
        }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private void Wake() { TaskCompletionSource old; lock (_gate) { old = _wake; _wake = NewWake(); } old.TrySetResult(); }
    private Task WakeTask() { lock (_gate) return _wake.Task; }
    private bool IsPaused() { lock (_gate) return _paused; }
    private int Passes() { lock (_gate) return _passes; }
    private bool IsSkipped(int row, int col) { lock (_gate) return _skipped.Contains((row, col)); }

    // ---- the pipeline -------------------------------------------------------------------------------------------

    private async Task RunAsync(MosaicRequest req, MosaicLayout layout, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string scope = req.ScopeId.Text;
        var visits = Channel.CreateBounded<Visit>(new BoundedChannelOptions(Math.Max(1, Lookahead)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var outcomes = Channel.CreateUnbounded<Outcome>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _visitQueue = visits.Reader;
        using var scopeState = new RemoteState<ScopeState>(_node, ScopeIds.State(scope), ScopeIds.GetState(scope));
        RemoteState<WeatherState>? weather = req.WeatherId.Text == "" ? null
            : new RemoteState<WeatherState>(_node, EquipmentIds.State(DeviceKinds.Weather, req.WeatherId.Text), EquipmentIds.GetState(DeviceKinds.Weather, req.WeatherId.Text));
        string endPhase = "Done", endMessage = "";
        try
        {
            await scopeState.StartAsync();
            if (scopeState.Latest is null) throw new InvalidOperationException($"scope {scope} not found");
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
                Task.Run(() => Stage(t => ProduceAsync(layout, visits.Writer, t))),
                Task.Run(() => Stage(t => ExecuteAsync(req, scope, scopeState, weather, visits.Reader, outcomes.Writer, t))),
                Task.Run(() => Stage(t => RecordAsync(outcomes.Reader, t))));
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
            weather?.Dispose();
            lock (_gate) { _cts = null; _paused = false; _visitQueue = null; }
            cts.Dispose();
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; s.QueueDepth = 0; s.CurrentRow = -1; s.CurrentCol = -1; });
        }
    }

    /// <summary>Producer: plans the route round after round. Re-reads the live settings before every visit.</summary>
    private async Task ProduceAsync(MosaicLayout layout, ChannelWriter<Visit> queue, CancellationToken ct)
    {
        try
        {
            int rows = layout.Rows.Value, cols = layout.Cols.Value;
            var panels = layout.Panels.ToDictionary(p => (p.Row.Value, p.Col.Value));
            for (int pass = 1; ; pass++)
            {
                int limit = Passes();
                if (limit != 0 && pass > limit) break;
                foreach (var (row, col) in MosaicGeometry.ScanOrder(rows, cols, pass))
                {
                    limit = Passes();
                    if (limit != 0 && pass > limit) goto finished;       // the number of rounds was lowered meanwhile
                    if (IsSkipped(row, col)) continue;
                    var p = panels[(row, col)];
                    await queue.WriteAsync(new Visit(pass, row, col, p.RaHours.Value, p.DecDegrees.Value), ct);
                    NoteDepth();
                }
            }
        finished:;
        }
        finally { queue.TryComplete(); }
    }

    private void NoteDepth()
    {
        int depth = _visitQueue?.Count ?? 0;
        if (depth > MaxQueueDepth) MaxQueueDepth = depth;
    }

    /// <summary>Executor: one visit = slew there and take one shot, through the scope.</summary>
    private async Task ExecuteAsync(MosaicRequest req, string scope, RemoteState<ScopeState> scopeState, RemoteState<WeatherState>? weather,
        ChannelReader<Visit> queue, ChannelWriter<Outcome> outcomes, CancellationToken ct)
    {
        try
        {
            await foreach (var visit in queue.ReadAllAsync(ct))
            {
                await WaitUntilAllowed(weather, ct);
                await Set(s => { s.Pass = visit.Pass; s.CurrentRow = visit.Row; s.CurrentCol = visit.Col; s.QueueDepth = _visitQueue?.Count ?? 0; if (s.Phase.Text != "Running") s.Phase = "Running"; s.Message = ""; });
                string error = await ShootAsync(req, scope, scopeState, visit, ct);
                await outcomes.WriteAsync(new Outcome(visit, error == "" ? null : error), ct);
                if (error != "") throw new InvalidOperationException(error);
            }
        }
        finally { outcomes.TryComplete(); }
    }

    private async Task<string> ShootAsync(MosaicRequest req, string scope, RemoteState<ScopeState> scopeState, Visit visit, CancellationToken ct)
    {
        var start = await Commands.CallAsync(_node, ScopeIds.Command(scope, "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = visit.RaHours, DecDegrees = visit.DecDegrees, Epoch = "J2000" },
            Exposure = req.Exposure, Count = 1, SlewTimeoutSeconds = req.SlewTimeoutSeconds.Value,
            ObjectName = $"{req.Label.Text}_r{visit.Row + 1}c{visit.Col + 1}", PlanId = req.Label.Text,
        });
        if (!start.Ok.Value) return start.Error.Text;
        var end = await scopeState.WaitAsync(s => !s.Observing.Value, TimeSpan.FromHours(6), ct);
        if (end.Phase.Text == "Error") return end.Message.Text != "" ? end.Message.Text : "the scope reported an error";
        return end.ShotsDone.Value >= 1 ? "" : "the shot was not taken";
    }

    /// <summary>Recorder: counts finished visits into the coverage map.</summary>
    private async Task RecordAsync(ChannelReader<Outcome> results, CancellationToken ct)
    {
        await foreach (var outcome in results.ReadAllAsync(ct))
        {
            if (outcome.Error is not null) continue;
            await Set(s => { lock (_gate) _frames[(outcome.Visit.Row, outcome.Visit.Col)] = _frames.GetValueOrDefault((outcome.Visit.Row, outcome.Visit.Col)) + 1; s.VisitsDone = s.VisitsDone.Value + 1; });
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
                Phase = _state.Phase.Text, Label = _state.Label.Text, Message = _state.Message.Text, Pass = _state.Pass.Value,
                Passes = _passes, VisitsDone = _state.VisitsDone.Value, QueueDepth = _state.QueueDepth.Value,
                CurrentRow = _state.CurrentRow.Value, CurrentCol = _state.CurrentCol.Value,
            };
            s.Layout.Rows = _layout.Rows.Value; s.Layout.Cols = _layout.Cols.Value;
            s.Layout.StepXDegrees = _layout.StepXDegrees.Value; s.Layout.StepYDegrees = _layout.StepYDegrees.Value;
            foreach (var p in _layout.Panels)
                s.Layout.Panels.Add(new MosaicPanel
                {
                    Row = p.Row.Value, Col = p.Col.Value, RaHours = p.RaHours.Value, DecDegrees = p.DecDegrees.Value,
                    Frames = _frames.GetValueOrDefault((p.Row.Value, p.Col.Value)), Skipped = _skipped.Contains((p.Row.Value, p.Col.Value)),
                });
            return s;
        }
    }

    private static MosaicRequest Copy(MosaicRequest r) => new()
    {
        Label = r.Label.Text, ScopeId = r.ScopeId.Text, WeatherId = r.WeatherId.Text, Passes = r.Passes.Value,
        Center = new SkyTarget { RaHours = r.Center.RaHours.Value, DecDegrees = r.Center.DecDegrees.Value, Epoch = r.Center.Epoch.Text },
        FovWidthDegrees = r.FovWidthDegrees.Value, FovHeightDegrees = r.FovHeightDegrees.Value, PositionAngleDegrees = r.PositionAngleDegrees.Value,
        FrameWidthDegrees = r.FrameWidthDegrees.Value, FrameHeightDegrees = r.FrameHeightDegrees.Value, Overlap = r.Overlap.Value,
        SlewTimeoutSeconds = r.SlewTimeoutSeconds.Value,
        Exposure = new ShooterExposure
        {
            Seconds = r.Exposure.Seconds.Value, FrameType = r.Exposure.FrameType.Text, Filter = r.Exposure.Filter.Text,
            BinX = r.Exposure.BinX.Value, BinY = r.Exposure.BinY.Value, Gain = r.Exposure.Gain.Value,
        },
    };

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
