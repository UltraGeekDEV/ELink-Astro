using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Runs a plan of observation blocks on a smart scope: for each block an optional autofocus, then the scope's
/// Observe. It knows the scope, the weather station and the autofocus service only by their EVent IDs. When the
/// weather turns Unsafe (or the plan is paused) it stops the scope, waits, and carries on with only the frames
/// still missing.</summary>
public sealed class SequencerService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<SequencerState> _publisher;
    private readonly object _gate = new();
    private SequencerState _state = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private bool _paused;
    private TaskCompletionSource _wake = NewWake();

    public SequencerService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, SequencerIds.State, SequencerIds.GetState, () => { lock (_gate) return Clone(_state); });
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync()
    {
        await _commands.AddAsync<SequencePlan, CommandResult>(SequencerIds.Start, StartPlan, "run a plan of observation blocks on a smart scope");
        await _commands.AddAsync<NOTESVoid, CommandResult>(SequencerIds.Pause, _ => Pause(true), "pause: stop the scope now, hold until Resume");
        await _commands.AddAsync<NOTESVoid, CommandResult>(SequencerIds.Resume, _ => Pause(false), "continue a paused plan");
        await _commands.AddAsync<NOTESVoid, CommandResult>(SequencerIds.Abort, _ => Abort(), "abort the plan");
        await _publisher.StartAsync();
    }

    private async Task<CommandResult> StartPlan(SequencePlan plan)
    {
        if (plan.ScopeId.Text == "") return CommandResult.Fail("ScopeId is required");
        if (plan.Blocks.Count == 0) return CommandResult.Fail("the plan has no blocks");
        foreach (var (b, i) in plan.Blocks.Select((b, i) => (b, i + 1)))
        {
            if (b.Count.Value < 1) return CommandResult.Fail($"block {i}: Count must be at least 1");
            if (!(b.Exposure.Seconds.Value >= 0)) return CommandResult.Fail($"block {i}: invalid exposure time");
            if (b.RefocusBefore.Value && (plan.Autofocus.ShooterId.Text == "" || plan.Autofocus.FocuserId.Text == ""))
                return CommandResult.Fail($"block {i} wants a refocus but the plan's Autofocus has no ShooterId/FocuserId");
        }
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null) return CommandResult.Fail("a plan is already running");
            cts = _cts = new CancellationTokenSource();
            _paused = false;
            _state = new SequencerState { Phase = "Running", PlanId = plan.Id.Text, BlockCount = plan.Blocks.Count };
        }
        await _publisher.PublishAsync();
        _run = Task.Run(() => RunAsync(Copy(plan), cts));
        return CommandResult.Success();
    }

    private async Task<CommandResult> Pause(bool pause)
    {
        lock (_gate)
        {
            if (_cts is null) return CommandResult.Fail("no plan is running");
            _paused = pause;
        }
        Wake();
        return CommandResult.Success();
    }

    private Task<CommandResult> Abort()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
        return Task.FromResult(CommandResult.Success());
    }

    private void Wake() { TaskCompletionSource old; lock (_gate) { old = _wake; _wake = NewWake(); } old.TrySetResult(); }
    private Task WakeTask() { lock (_gate) return _wake.Task; }

    private async Task Set(Action<SequencerState> change)
    {
        lock (_gate) change(_state);
        await _publisher.PublishAsync();
    }

    private async Task RunAsync(SequencePlan plan, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string scope = plan.ScopeId.Text;
        using var scopeState = new RemoteState<ScopeState>(_node, ScopeIds.State(scope), ScopeIds.GetState(scope));
        RemoteState<WeatherState>? weather = plan.WeatherId.Text == "" ? null
            : new RemoteState<WeatherState>(_node, EquipmentIds.State(DeviceKinds.Weather, plan.WeatherId.Text), EquipmentIds.GetState(DeviceKinds.Weather, plan.WeatherId.Text));
        RemoteState<AutofocusState>? focus = null;
        string endPhase = "Done", endMessage = "";
        try
        {
            await scopeState.StartAsync();
            if (scopeState.Latest is null) throw new InvalidOperationException($"scope {scope} not found");
            if (weather is not null) { await weather.StartAsync(); weather.Changed += _ => Wake(); }
            if (plan.Blocks.Any(b => b.RefocusBefore.Value)) { focus = new RemoteState<AutofocusState>(_node, AutofocusIds.State, AutofocusIds.GetState); await focus.StartAsync(); }

            int total = 0;
            for (int bi = 0; bi < plan.Blocks.Count; bi++)
            {
                var block = plan.Blocks[bi];
                int index = bi + 1, count = block.Count.Value;
                await Set(s => { s.BlockIndex = index; s.BlockLabel = block.Label.Text != "" ? block.Label.Text : $"block {index}"; s.ShotsInBlock = 0; s.ShotsPlannedInBlock = count; s.Message = ""; });

                bool focused = !block.RefocusBefore.Value;
                int remaining = count;
                while (remaining > 0)
                {
                    await WaitUntilAllowed(weather, ct);
                    if (!focused)
                    {
                        await Set(s => s.Phase = "Focusing");
                        await RunAutofocus(plan.Autofocus, focus!, ct);
                        focused = true;
                    }
                    await Set(s => s.Phase = "Running");
                    int doneBefore = count - remaining, totalBefore = total;
                    int done = await ObserveOnce(scope, plan.Id.Text, scopeState, weather, block, remaining, doneBefore, totalBefore, ct);
                    remaining -= done; total += done;
                    int inBlock = count - remaining, all = total;
                    await Set(s => { s.ShotsInBlock = inBlock; s.ShotsTotal = all; });
                }
            }
        }
        catch (OperationCanceledException)
        {
            endPhase = "Aborted"; endMessage = "aborted";
            await Commands.CallAsync(_node, ScopeIds.Command(scope, "Abort"), NOTESVoid.Void);
            await Commands.CallAsync(_node, AutofocusIds.Abort, NOTESVoid.Void);
        }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            weather?.Dispose(); focus?.Dispose();
            lock (_gate) { _cts = null; _paused = false; }
            cts.Dispose();
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; s.BlockIndex = endPhase == "Done" ? s.BlockCount.Value : s.BlockIndex.Value; });
        }
    }

    private bool Unsafe(RemoteState<WeatherState>? weather) => weather?.Latest is { Safety.Text: "Unsafe" };
    private bool IsPaused() { lock (_gate) return _paused; }

    /// <summary>Holds while the plan is paused or the weather is unsafe.</summary>
    private async Task WaitUntilAllowed(RemoteState<WeatherState>? weather, CancellationToken ct)
    {
        while (IsPaused() || Unsafe(weather))
        {
            string phase = IsPaused() ? "Paused" : "WaitingForWeather";
            await Set(s => { s.Phase = phase; s.Message = phase == "Paused" ? "paused" : "weather is unsafe"; });
            var wake = WakeTask();
            if (!IsPaused() && !Unsafe(weather)) break;
            await wake.WaitAsync(ct);
        }
        await Set(s => s.Message = "");
    }

    /// <summary>One Observe call. Returns the exposure rounds that finished; fewer than asked if interrupted.</summary>
    private async Task<int> ObserveOnce(string scope, string planId, RemoteState<ScopeState> scopeState, RemoteState<WeatherState>? weather, SequenceBlock block, int count,
        int doneBefore, int totalBefore, CancellationToken ct)
    {
        // show the scope's progress live: rounds finished so far in this block and in the plan
        Action<ScopeState> progress = st => { if (st.Observing.Value) _ = Set(x => { x.ShotsInBlock = doneBefore + st.ShotsDone.Value; x.ShotsTotal = totalBefore + st.ShotsDone.Value; }); };
        scopeState.Changed += progress;
        try { return await ObserveCore(scope, planId, scopeState, weather, block, count, ct); }
        finally { scopeState.Changed -= progress; }
    }

    private async Task<int> ObserveCore(string scope, string planId, RemoteState<ScopeState> scopeState, RemoteState<WeatherState>? weather, SequenceBlock block, int count, CancellationToken ct)
    {
        var start = await Commands.CallAsync(_node, ScopeIds.Command(scope, "Observe"), new ObserveRequest
        {
            Target = block.Target, Exposure = block.Exposure, Count = count, SlewTimeoutSeconds = 600,
            ObjectName = block.Label.Text, PlanId = planId,
        });
        if (!start.Ok.Value) throw new InvalidOperationException(start.Error.Text);

        var ended = scopeState.WaitAsync(s => !s.Observing.Value, TimeSpan.FromDays(2), ct);
        while (true)
        {
            var wake = WakeTask();
            if (IsPaused() || Unsafe(weather)) break;
            if (await Task.WhenAny(ended, wake) == ended)
            {
                var final = await ended;
                if (final.Phase.Text == "Error") throw new InvalidOperationException(final.Message.Text != "" ? final.Message.Text : "the scope reported an error");
                return final.ShotsDone.Value;
            }
        }

        // interrupted (pause or unsafe weather): stop the scope, count what was finished
        await Commands.CallAsync(_node, ScopeIds.Command(scope, "Abort"), NOTESVoid.Void);
        var stopped = await scopeState.WaitAsync(s => !s.Observing.Value, TimeSpan.FromSeconds(120), ct);
        return stopped.ShotsDone.Value;
    }

    private async Task RunAutofocus(AutofocusRequest req, RemoteState<AutofocusState> focus, CancellationToken ct)
    {
        var start = await Commands.CallAsync(_node, AutofocusIds.Run, req);
        if (!start.Ok.Value) throw new InvalidOperationException("autofocus: " + start.Error.Text);
        var end = await focus.WaitAsync(s => s.Phase.Text is "Done" or "Error" or "Aborted", TimeSpan.FromMinutes(30), ct);
        if (end.Phase.Text != "Done") throw new InvalidOperationException("autofocus failed: " + (end.Message.Text != "" ? end.Message.Text : end.Phase.Text));
    }

    private static SequenceBlock CopyBlock(SequenceBlock b) => new()
    {
        Label = b.Label.Text, Count = b.Count.Value, RefocusBefore = b.RefocusBefore.Value,
        Target = new SkyTarget { RaHours = b.Target.RaHours.Value, DecDegrees = b.Target.DecDegrees.Value, Epoch = b.Target.Epoch.Text },
        Exposure = new ShooterExposure
        {
            Seconds = b.Exposure.Seconds.Value, FrameType = b.Exposure.FrameType.Text, Filter = b.Exposure.Filter.Text,
            BinX = b.Exposure.BinX.Value, BinY = b.Exposure.BinY.Value, Gain = b.Exposure.Gain.Value,
        },
    };

    private static SequencePlan Copy(SequencePlan p)
    {
        var c = new SequencePlan
        {
            Id = p.Id.Text, ScopeId = p.ScopeId.Text, WeatherId = p.WeatherId.Text,
            Autofocus = new AutofocusRequest
            {
                ShooterId = p.Autofocus.ShooterId.Text, FocuserId = p.Autofocus.FocuserId.Text, ExposureSeconds = p.Autofocus.ExposureSeconds.Value,
                StepSize = p.Autofocus.StepSize.Value, Samples = p.Autofocus.Samples.Value, Filter = p.Autofocus.Filter.Text,
            },
        };
        foreach (var b in p.Blocks) c.Blocks.Add(CopyBlock(b));
        return c;
    }

    private static SequencerState Clone(SequencerState s) => new()
    {
        Phase = s.Phase.Text, PlanId = s.PlanId.Text, BlockIndex = s.BlockIndex.Value, BlockCount = s.BlockCount.Value, BlockLabel = s.BlockLabel.Text,
        ShotsInBlock = s.ShotsInBlock.Value, ShotsPlannedInBlock = s.ShotsPlannedInBlock.Value, ShotsTotal = s.ShotsTotal.Value, Message = s.Message.Text,
    };

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
