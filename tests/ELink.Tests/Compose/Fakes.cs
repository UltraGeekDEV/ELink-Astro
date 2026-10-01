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
                    Shooter = _id, Format = Fits ? ".fits" : ".fake", ExposureSeconds = e.Seconds, FrameType = e.FrameType, Filter = e.Filter,
                    Data = new RawBytes(Fits ? ELink.Imaging.FitsImage.Write16(16, 16, new ushort[256]) : new byte[] { 1, 2, 3 }),
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
