using ELink.Contracts;
using EVent.Connections.Models.BaseBinaryConvertibles;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Tests.Compose;

/// <summary>A pointer that exists only as EVent endpoints: proves a scope needs nothing but the contract.</summary>
public sealed class FakePointer : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id;
    private readonly int _slewMs;
    private readonly CommandSet _cmds;
    private readonly StatePublisher<PointerState> _pub;
    private PointerState _state = new() { Phase = "Idle" };
    public List<SkyTarget> Gotos { get; } = new();
    /// <summary>When set, a Goto ends in this phase instead of OnTarget (e.g. Parked).</summary>
    public string? StuckPhase { get; set; }

    public FakePointer(TypeSafeEVentNode node, string id, int slewMs = 150)
    {
        _node = node; _id = id; _slewMs = slewMs;
        _cmds = new CommandSet(node);
        _pub = new(node, PointerIds.State(id), PointerIds.GetState(id), () => new PointerState
        { Phase = _state.Phase.Text, RaHours = _state.RaHours.Value, DecDegrees = _state.DecDegrees.Value, OnTarget = _state.OnTarget.Value });
    }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<SkyTarget, CommandResult>(PointerIds.Goto(_id), async t =>
        {
            Gotos.Add(t);
            _state = new PointerState { Phase = "Slewing", RaHours = _state.RaHours.Value, DecDegrees = _state.DecDegrees.Value };
            await _pub.PublishAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(_slewMs);
                _state = StuckPhase is { } stuck
                    ? new PointerState { Phase = stuck }
                    : new PointerState { Phase = "OnTarget", RaHours = t.RaHours.Value, DecDegrees = t.DecDegrees.Value, OnTarget = true };
                await _pub.PublishAsync();
            });
            return CommandResult.Success();
        }, "fake goto");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(PointerIds.Abort(_id), _ => Task.FromResult(CommandResult.Success()), "fake abort");
        await _pub.StartAsync();
    }

    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class FakeShooter : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id;
    private readonly CommandSet _cmds;
    private readonly StatePublisher<ShooterState> _pub;
    private string _phase = "Idle";
    public int Exposures;
    public List<ShooterExposure> Requests { get; } = new();
    /// <summary>Deliver real FITS frames instead of three dummy bytes.</summary>
    public bool Fits { get; set; }
    /// <summary>Makes the frame for exposure number n (from 1): e.g. a synthetic star field; overrides Fits.</summary>
    public Func<int, byte[]>? FrameMaker { get; set; }

    public FakeShooter(TypeSafeEVentNode node, string id)
    {
        _node = node; _id = id;
        _cmds = new CommandSet(node);
        _pub = new(node, ShooterIds.State(id), ShooterIds.GetState(id), () => new ShooterState { Phase = _phase });
    }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), async e =>
        {
            Requests.Add(e); Interlocked.Increment(ref Exposures);
            _phase = "Exposing"; await _pub.PublishAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay((int)(e.Seconds.Value * 1000));
                await _node.FireEventAsync(ShooterIds.Shot(_id), new ShotEvent
                {
                    Shooter = _id, Format = Fits || FrameMaker is not null ? ".fits" : ".fake", ExposureSeconds = e.Seconds, FrameType = e.FrameType, Filter = e.Filter,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Data = new RawBytes(FrameMaker is { } make ? make(Exposures) : Fits ? ELink.Imaging.FitsImage.Write16(16, 16, new ushort[256]) : new byte[] { 1, 2, 3 }),
                });
                _phase = "Idle"; await _pub.PublishAsync();
            });
            return CommandResult.Success();
        }, "fake expose");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ => Task.FromResult(CommandResult.Success()), "fake abort");
        await _pub.StartAsync();
    }

    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A weather station that is only EVent endpoints; tests flip its verdict.</summary>
public sealed class FakeWeather : IAsyncDisposable
{
    private readonly StatePublisher<WeatherState> _pub;
    private string _safety = "Safe";
    public FakeWeather(TypeSafeEVentNode node, string id)
    {
        _pub = new(node, EquipmentIds.State(DeviceKinds.Weather, id), EquipmentIds.GetState(DeviceKinds.Weather, id),
            () => new WeatherState { Connected = true, Safety = _safety, Safe = _safety == "Safe" });
    }
    public Task StartAsync() => _pub.StartAsync();
    public Task SetAsync(string safety) { _safety = safety; return _pub.PublishAsync(); }
    public ValueTask DisposeAsync() { _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Stands in for the autofocus service: Run takes a moment, then reports Done (or Error).</summary>
public sealed class FakeAutofocus : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<ELink.Contracts.Automation.AutofocusState> _pub;
    private string _phase = "Idle";
    public int Runs;
    public bool Fail;
    public List<ELink.Contracts.Automation.AutofocusRequest> Requests { get; } = new();
    public FakeAutofocus(TypeSafeEVentNode node)
    {
        _cmds = new CommandSet(node);
        _pub = new(node, ELink.Contracts.Automation.AutofocusIds.State, ELink.Contracts.Automation.AutofocusIds.GetState,
            () => new ELink.Contracts.Automation.AutofocusState { Phase = _phase, Message = _phase == "Error" ? "no stars" : "" });
    }
    public async Task StartAsync()
    {
        await _cmds.AddAsync<ELink.Contracts.Automation.AutofocusRequest, CommandResult>(ELink.Contracts.Automation.AutofocusIds.Run, async req =>
        {
            Interlocked.Increment(ref Runs);
            _phase = "Moving"; await _pub.PublishAsync();
            _ = Task.Run(async () => { await Task.Delay(150); _phase = Fail ? "Error" : "Done"; await _pub.PublishAsync(); });
            return CommandResult.Success();
        }, "fake autofocus");
        await _cmds.AddAsync<ELink.Contracts.Automation.AutofocusRequest, ELink.Contracts.Automation.AutofocusState>(ELink.Contracts.Automation.AutofocusIds.RunAndWait, async req =>
        {
            Interlocked.Increment(ref Runs);
            lock (Requests) Requests.Add(req);
            await Task.Delay(100);
            return new ELink.Contracts.Automation.AutofocusState { Phase = Fail ? "Error" : "Done", Message = Fail ? "no stars" : "", FocuserId = req.FocuserId.Text, BestPosition = 5000 };
        }, "fake autofocus, waiting");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(ELink.Contracts.Automation.AutofocusIds.Abort, _ => Task.FromResult(CommandResult.Success()), "fake abort");
        await _pub.StartAsync();
    }
    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A rotator that exists only as EVent endpoints; records every angle it was asked to go to.</summary>
public sealed class FakeRotator : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<RotatorState> _pub;
    private readonly string _id;
    private double _angle; private bool _moving;
    public List<double> Moves { get; } = new();

    public FakeRotator(TypeSafeEVentNode node, string id)
    {
        _id = id; _cmds = new CommandSet(node);
        _pub = new(node, EquipmentIds.State(DeviceKinds.Rotator, id), EquipmentIds.GetState(DeviceKinds.Rotator, id),
            () => new RotatorState { Connected = true, AngleDegrees = _angle, Moving = _moving });
    }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<BinaryConvertibleDouble, CommandResult>(EquipmentIds.Command(DeviceKinds.Rotator, _id, "MoveTo"), async a =>
        {
            Moves.Add(a.Value);
            _moving = true; await _pub.PublishAsync();
            _ = Task.Run(async () => { await Task.Delay(40); _angle = a.Value; _moving = false; await _pub.PublishAsync(); });
            return CommandResult.Success();
        }, "fake rotate");
        await _pub.StartAsync();
    }

    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A mount with a real pointing error: it reports R = X + e for its true axis X. Goto(T) makes R = T; Sync(S) sets what it
/// reports without moving it (e = S − X). With <see cref="SyncDoesNotHelp"/> it behaves like the INDI simulator, whose field follows
/// whatever it reports, so a sync shifts the field too and only aiming off can correct.</summary>
public sealed class FakeMount : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id;
    private readonly CommandSet _cmds;
    private readonly StatePublisher<MountState> _pub;
    private readonly object _gate = new();
    public (double Ra, double Dec) Reported = (5, 10);
    public (double E, double N) ErrorDeg;             // pointing error: true = reported shifted by -error (tangent plane)
    public (double Ra, double Dec)? Target;
    public string Phase = "Tracking";
    public string PierSide = "West";
    public bool SyncDoesNotHelp;
    public int Gotos, Syncs;

    public FakeMount(TypeSafeEVentNode node, string id)
    {
        _node = node; _id = id; _cmds = new CommandSet(node);
        _pub = new(node, EquipmentIds.State(DeviceKinds.Mount, id), EquipmentIds.GetState(DeviceKinds.Mount, id), () =>
        {
            lock (_gate) return new MountState
            {
                Connected = true, RaHours = Reported.Ra, DecDegrees = Reported.Dec, Epoch = "J2000", Phase = Phase, Tracking = Phase == "Tracking", PierSide = PierSide,
                TargetRaHours = Target?.Ra ?? double.NaN, TargetDecDegrees = Target?.Dec ?? double.NaN,
            };
        });
    }

    /// <summary>Where the mount really points.</summary>
    public (double Ra, double Dec) True { get { lock (_gate) return ELink.Core.Astro.Gnomonic.ToSky(Reported.Ra, Reported.Dec, -ErrorDeg.E, -ErrorDeg.N); } }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<SkyTarget, CommandResult>(EquipmentIds.Command(DeviceKinds.Mount, _id, "Goto"), async t =>
        {
            Interlocked.Increment(ref Gotos);
            await SlewAsync(t.RaHours.Value, t.DecDegrees.Value);
            return CommandResult.Success();
        }, "fake goto");
        await _cmds.AddAsync<SkyTarget, CommandResult>(EquipmentIds.Command(DeviceKinds.Mount, _id, "Sync"), async t =>
        {
            Interlocked.Increment(ref Syncs);
            lock (_gate)
            {
                if (!SyncDoesNotHelp)
                {
                    // "you are at S": keep the true axis, change what is reported
                    (double Ra, double Dec) truth = ELink.Core.Astro.Gnomonic.ToSky(Reported.Ra, Reported.Dec, -ErrorDeg.E, -ErrorDeg.N);
                    var (e, n) = ELink.Core.Astro.Gnomonic.FromSky(truth.Ra, truth.Dec, t.RaHours.Value, t.DecDegrees.Value);
                    ErrorDeg = (e, n);
                }
                Reported = (t.RaHours.Value, t.DecDegrees.Value);
            }
            await _pub.PublishAsync();
            return CommandResult.Success();
        }, "fake sync");
        await _pub.StartAsync();
    }

    /// <summary>Like pressing goto on a hand controller: the mount moves; ELink did not ask for it.</summary>
    public Task HandControllerGotoAsync(double ra, double dec) => SlewAsync(ra, dec);

    private async Task SlewAsync(double ra, double dec)
    {
        lock (_gate) { Target = (ra, dec); Phase = "Slewing"; }
        await _pub.PublishAsync();
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            lock (_gate) { Reported = (ra, dec); Phase = "Tracking"; }
            await _pub.PublishAsync();
        });
    }

    public Task PublishAsync() => _pub.PublishAsync();
    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A plate solver that knows where each camera really looks: the guide scope along the mount's true axis, the primary at a
/// fixed offset from it (turned over on the other pier side).</summary>
public sealed class FakeSolver : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly FakeMount _mount;
    public string GuideId = "guide", PrimaryId = "main";
    public (double E, double N) PrimaryOffsetDeg;     // on the West pier side
    public (double E, double N) SimLikeFieldShiftDeg; // with SyncDoesNotHelp: the field is the reported position shifted by this
    public bool Fail;
    public int Solves;

    public FakeSolver(TypeSafeEVentNode node, FakeMount mount) { _mount = mount; _cmds = new CommandSet(node); }

    public Task StartAsync() => _cmds.AddAsync<ELink.Contracts.Automation.SolveRequest, ELink.Contracts.Automation.SolveResult>(ELink.Contracts.Automation.SolveIds.Solve, r =>
    {
        Interlocked.Increment(ref Solves);
        if (Fail) return Task.FromResult(new ELink.Contracts.Automation.SolveResult { Message = "no solution" });
        (double Ra, double Dec) axis = _mount.SyncDoesNotHelp
            ? ELink.Core.Astro.Gnomonic.ToSky(_mount.Reported.Ra, _mount.Reported.Dec, SimLikeFieldShiftDeg.E, SimLikeFieldShiftDeg.N)
            : _mount.True;
        var at = axis;
        if (r.ShooterId.Text == PrimaryId)
        {
            double s = _mount.PierSide == "East" ? -1 : 1;
            at = ELink.Core.Astro.Gnomonic.ToSky(axis.Ra, axis.Dec, s * PrimaryOffsetDeg.E, s * PrimaryOffsetDeg.N);
        }
        return Task.FromResult(new ELink.Contracts.Automation.SolveResult { Solved = true, RaHours = at.Ra, DecDegrees = at.Dec, ShooterId = r.ShooterId.Text });
    }, "fake solver");

    public ValueTask DisposeAsync() { _cmds.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A guider that is only EVent endpoints: records what the scope asks of it.</summary>
public sealed class FakeGuider : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<GuiderState> _pub;
    private string _phase = "Idle";
    public List<string> Calls { get; } = new();
    public bool FailStart { get; set; }
    public bool NeverSettles { get; set; }

    public FakeGuider(TypeSafeEVentNode node, string id)
    {
        _cmds = new CommandSet(node);
        Id = id;
        _pub = new(node, GuiderIds.State(id), GuiderIds.GetState(id), () => new GuiderState { Phase = _phase, Settled = _phase == "Guiding" });
    }

    public string Id { get; }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<GuideStartRequest, CommandResult>(GuiderIds.Start(Id), async _ =>
        {
            lock (Calls) Calls.Add("Start");
            if (FailStart) { _phase = "Error"; await _pub.PublishAsync(); return CommandResult.Fail("no guide star"); }
            _phase = "Guiding"; await _pub.PublishAsync();
            return CommandResult.Success();
        }, "fake guide start");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(GuiderIds.Stop(Id), async _ =>
        {
            lock (Calls) Calls.Add("Stop");
            _phase = "Idle"; await _pub.PublishAsync();
            return CommandResult.Success();
        }, "fake guide stop");
        await _cmds.AddAsync<DitherRequest, CommandResult>(GuiderIds.Dither(Id), async r =>
        {
            lock (Calls) Calls.Add(r.Pixels.Value > 0 ? $"Dither {r.Pixels.Value:0.#}" : "Settle");
            await Task.Delay(30);
            if (_phase != "Guiding") return CommandResult.Fail("not guiding");
            return NeverSettles ? CommandResult.Fail("guiding did not settle") : CommandResult.Success();
        }, "fake dither");
        await _pub.StartAsync();
    }

    public List<string> Snapshot() { lock (Calls) return Calls.ToList(); }

    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}
