using System.Globalization;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>A smart scope: "point to, shoot at". It follows any number of Pointers and Shooters (only by their EVent
/// IDs) and presents itself as a Pointer and as a Shooter under its own id, so a scope can be a part of another scope.
/// <c>Observe</c> runs the whole job: point, wait until settled, take the requested exposures.</summary>
public sealed class SmartScope : IAsyncDisposable
{
    private sealed record PointerRef(string Id, RemoteState<PointerState> State);
    private sealed record ShooterRef(string Id, double East, double North, RemoteState<ShooterState> State, Action<ShotEvent> OnShot);

    private readonly TypeSafeEVentNode _node;
    private readonly ScopeDefinition _def;
    private readonly string _id;
    private readonly List<PointerRef> _pointers = new();
    private readonly List<ShooterRef> _shooters = new();
    private readonly CommandSet _commands;
    private readonly StatePublisher<PointerState> _pointerPub;
    private readonly StatePublisher<ShooterState> _shooterPub;
    private readonly StatePublisher<ScopeState> _scopePub;
    private readonly SemaphoreSlim _shots = new(0);
    private readonly object _scopeGate = new();
    private ScopeState _scope = new();
    private CancellationTokenSource? _observe;
    private Task? _observeTask;

    public SmartScope(TypeSafeEVentNode node, ScopeDefinition definition)
    {
        _node = node; _def = definition; _id = definition.Id.Text;
        foreach (var p in definition.Pointers)
            _pointers.Add(new PointerRef(p.Text, new RemoteState<PointerState>(node, PointerIds.State(p.Text), PointerIds.GetState(p.Text))));
        foreach (var s in definition.Shooters)
        {
            string sid = s.Id.Text;
            _shooters.Add(new ShooterRef(sid, s.OffsetEastArcmin.Value, s.OffsetNorthArcmin.Value,
                new RemoteState<ShooterState>(node, ShooterIds.State(sid), ShooterIds.GetState(sid)), shot => _ = RelayShotAsync(shot)));
        }
        _commands = new CommandSet(node);
        _pointerPub = new(node, PointerIds.State(_id), PointerIds.GetState(_id), BuildPointer);
        _shooterPub = new(node, ShooterIds.State(_id), ShooterIds.GetState(_id), BuildShooter);
        _scopePub = new(node, ScopeIds.State(_id), ScopeIds.GetState(_id), () => { lock (_scopeGate) return Clone(_scope); });
    }

    public string Id => _id;
    public ScopeDefinition Definition => _def;

    public async Task StartAsync()
    {
        foreach (var p in _pointers) { await p.State.StartAsync(); p.State.Changed += __ => { _ = _pointerPub.PublishAsync(); _ = Task.Run(RefreshScopeAsync); }; }
        foreach (var s in _shooters)
        {
            await s.State.StartAsync();
            s.State.Changed += __ => { _ = _shooterPub.PublishAsync(); };
            await _node.HookEventAsync(ShooterIds.Shot(s.Id), s.OnShot, "shot relay");
        }

        await _commands.AddAsync<SkyTarget, CommandResult>(PointerIds.Goto(_id), GotoAsync, $"point every pointer of scope {_id} at a position");
        await _commands.AddAsync<NOTESVoid, CommandResult>(PointerIds.Abort(_id), _ => AbortPointersAsync(), "stop all pointers");
        await _commands.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), ExposeAsync, $"expose with every shooter of scope {_id}");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ => AbortShootersAsync(), "abort all exposures");
        await _commands.AddAsync<ObserveRequest, CommandResult>(ScopeIds.Command(_id, "Observe"), ObserveAsync, "point at a target, wait until settled, then take the exposures");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ScopeIds.Command(_id, "Abort"), _ => AbortAllAsync(), "stop the observation, all pointers and all shooters");

        await _pointerPub.StartAsync();
        await _shooterPub.StartAsync();
        await _scopePub.StartAsync();
    }

    // ---- Pointer interface ------------------------------------------------------------------------------------

    private async Task<CommandResult> GotoAsync(SkyTarget target)
    {
        if (_pointers.Count == 0) return CommandResult.Fail($"scope {_id} has no pointer");
        double ra = target.RaHours.Value, dec = target.DecDegrees.Value;
        string epoch = target.Epoch.Text;
        // Put the primary shooter's centre, not the pointing axis, on the target.
        if (_shooters.Count > 0 && (_shooters[0].East != 0 || _shooters[0].North != 0))
            (ra, dec) = Sky.Offset(ra, dec, -_shooters[0].East, -_shooters[0].North);
        var adjusted = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = epoch };
        var results = await Task.WhenAll(_pointers.Select(p => Commands.CallAsync(_node, PointerIds.Goto(p.Id), adjusted)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task<CommandResult> AbortPointersAsync()
    {
        var results = await Task.WhenAll(_pointers.Select(p => Commands.CallAsync(_node, PointerIds.Abort(p.Id), NOTESVoid.Void)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private PointerState BuildPointer()
    {
        var states = _pointers.Select(p => p.State.Latest).ToList();
        var s = new PointerState();
        if (states.Count == 0 || states.Any(x => x is null) || states.Any(x => x!.Phase.Text == "Disconnected")) { s.Phase = "Disconnected"; return s; }
        var first = states[0]!;
        s.RaHours = first.RaHours; s.DecDegrees = first.DecDegrees;
        s.OnTarget = states.All(x => x!.OnTarget.Value);
        s.Phase = states.Any(x => x!.Phase.Text == "Error") ? "Error"
            : states.Any(x => x!.Phase.Text == "Slewing") ? "Slewing"
            : s.OnTarget ? "OnTarget"
            : states.Any(x => x!.Phase.Text == "Parked") ? "Parked" : "Idle";
        s.Message = states.Select(x => x!.Message.Text).FirstOrDefault(m => m != "") ?? "";
        return s;
    }

    // ---- Shooter interface ------------------------------------------------------------------------------------

    private async Task<CommandResult> ExposeAsync(ShooterExposure e)
    {
        if (_shooters.Count == 0) return CommandResult.Fail($"scope {_id} has no shooter");
        var results = await Task.WhenAll(_shooters.Select(s => Commands.CallAsync(_node, ShooterIds.Expose(s.Id), e)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task<CommandResult> AbortShootersAsync()
    {
        var results = await Task.WhenAll(_shooters.Select(s => Commands.CallAsync(_node, ShooterIds.Abort(s.Id), NOTESVoid.Void)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private ShooterState BuildShooter()
    {
        var states = _shooters.Select(s => s.State.Latest).ToList();
        var s = new ShooterState();
        if (states.Count == 0 || states.All(x => x is null || x.Phase.Text == "Disconnected")) { s.Phase = "Disconnected"; return s; }
        var live = states.Where(x => x is not null && x.Phase.Text != "Disconnected").Select(x => x!).ToList();
        s.Phase = live.Any(x => x.Phase.Text == "Error") ? "Error" : live.Any(x => x.Phase.Text == "Exposing") ? "Exposing" : "Idle";
        s.ExposureRemaining = live.Max(x => x.ExposureRemaining.Value);
        s.Filter = live.Select(x => x.Filter.Text).FirstOrDefault(f => f != "") ?? "";
        s.ShotsPerExposure = states.Sum(x => x?.ShotsPerExposure.Value ?? 1);
        s.Message = live.Select(x => x.Message.Text).FirstOrDefault(m => m != "") ?? "";
        return s;
    }

    /// <summary>A child shooter delivered a frame: note it for a running Observe and pass it on as our own, with pointing.</summary>
    private async Task RelayShotAsync(ShotEvent shot)
    {
        _shots.Release();
        var p = _pointers.Count > 0 ? _pointers[0].State.Latest : null;
        var relayed = new ShotEvent
        {
            Shooter = shot.Shooter, Format = shot.Format, ExposureSeconds = shot.ExposureSeconds, FrameType = shot.FrameType,
            Filter = shot.Filter, Timestamp = shot.Timestamp, Data = shot.Data,
            PointingRaHours = double.IsNaN(shot.PointingRaHours.Value) ? (p?.RaHours.Value ?? double.NaN) : shot.PointingRaHours.Value,
            PointingDecDegrees = double.IsNaN(shot.PointingDecDegrees.Value) ? (p?.DecDegrees.Value ?? double.NaN) : shot.PointingDecDegrees.Value,
        };
        try { await _node.FireEventAsync(ShooterIds.Shot(_id), relayed); } catch (ObjectDisposedException) { }
    }

    // ---- Scope: the whole job --------------------------------------------------------------------------------

    private Task<CommandResult> AbortAllAsync()
    {
        return Task.Run(async () =>
        {
            try { _observe?.Cancel(); } catch (ObjectDisposedException) { }
            var a = await AbortPointersAsync();
            var b = await AbortShootersAsync();
            return !a.Ok.Value ? a : b;
        });
    }

    private async Task<CommandResult> ObserveAsync(ObserveRequest r)
    {
        var rejected = StartObserve(r);
        if (rejected is not null) return rejected;
        // Publish "observing" before answering: whoever waits for the run to end must not see the previous run's state.
        await _scopePub.PublishAsync();
        return CommandResult.Success();   // accepted; progress arrives as scope state events
    }

    private CommandResult? StartObserve(ObserveRequest r)
    {
        if (r.Count.Value < 1) return CommandResult.Fail("Count must be at least 1");
        if (_pointers.Count == 0) return CommandResult.Fail($"scope {_id} has no pointer");
        if (_shooters.Count == 0) return CommandResult.Fail($"scope {_id} has no shooter");
        lock (_scopeGate)
        {
            if (_observe is not null) return CommandResult.Fail("already observing");
            _observe = new CancellationTokenSource();
            _scope = new ScopeState { Phase = "Pointing", Observing = true, ShotsPlanned = r.Count.Value, ShotsDone = 0 };
        }
        var cts = _observe;
        _observeTask = Task.Run(() => RunObserveAsync(r, cts!));
        return null;
    }

    private async Task RunObserveAsync(ObserveRequest r, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string endPhase = "Idle", endMessage = "";
        try
        {
            await SetScope(s => { s.Phase = "Pointing"; s.Message = ""; });
            var go = await GotoAsync(r.Target);
            if (!go.Ok.Value) throw new InvalidOperationException(go.Error.Text);
            var slewTimeout = TimeSpan.FromSeconds(Math.Max(10, r.SlewTimeoutSeconds.Value));
            // wait for every pointer to settle, but give up early when one cannot (parked, failed, gone)
            var settled = await Task.WhenAll(_pointers.Select(p => p.State.WaitAsync(
                s => s.OnTarget.Value || s.Phase.Text is "Error" or "Parked" or "Disconnected", slewTimeout, ct)));
            var failed = settled.FirstOrDefault(s => !s.OnTarget.Value);
            if (failed is not null)
                throw new InvalidOperationException($"a pointer cannot reach the target: {failed.Phase.Text}" + (failed.Message.Text != "" ? $" ({failed.Message.Text})" : ""));
            await SetScope(s => s.Phase = "OnTarget");

            int perRound = Math.Max(1, _shooters.Sum(s => s.State.Latest?.ShotsPerExposure.Value ?? 1));
            for (int i = 0; i < r.Count.Value; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (_shots.CurrentCount > 0) _shots.Wait(0); // forget frames from before this round
                await SetScope(s => s.Phase = "Exposing");
                var ex = await ExposeAsync(r.Exposure);
                if (!ex.Ok.Value) throw new InvalidOperationException(ex.Error.Text);
                var roundTimeout = TimeSpan.FromSeconds(r.Exposure.Seconds.Value + 180);
                for (int k = 0; k < perRound; k++)
                    if (!await _shots.WaitAsync(roundTimeout, ct)) throw new TimeoutException("a frame did not arrive in time");
                int done = i + 1;
                await SetScope(s => s.ShotsDone = done);
            }
            await SetScope(s => s.Phase = "OnTarget");
        }
        catch (OperationCanceledException) { endMessage = "aborted"; }
        catch (TimeoutException ex) { endPhase = "Error"; endMessage = ex.Message; }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            lock (_scopeGate) { _observe = null; }
            cts.Dispose();
            await SetScope(s => { s.Observing = false; s.Phase = endPhase; s.Message = endMessage; });
        }
    }

    private async Task SetScope(Action<ScopeState> change)
    {
        lock (_scopeGate) change(_scope);
        await _scopePub.PublishAsync();
    }

    private async Task RefreshScopeAsync() => await _scopePub.PublishAsync();

    private static ScopeState Clone(ScopeState s) => new()
    {
        Phase = s.Phase.Text, Message = s.Message.Text, ShotsDone = s.ShotsDone.Value, ShotsPlanned = s.ShotsPlanned.Value, Observing = s.Observing.Value,
    };

    public async ValueTask DisposeAsync()
    {
        try { _observe?.Cancel(); } catch (ObjectDisposedException) { }
        if (_observeTask is not null) await Task.WhenAny(_observeTask, Task.Delay(2000));
        _commands.Dispose(); _pointerPub.Dispose(); _shooterPub.Dispose(); _scopePub.Dispose();
        foreach (var p in _pointers) p.State.Dispose();
        foreach (var s in _shooters)
        {
            s.State.Dispose();
            try { _node.UnhookEvent(ShooterIds.Shot(s.Id), s.OnShot); } catch (ObjectDisposedException) { }
        }
    }
}
