using ELink.Contracts;
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
                { Shooter = _id, Format = ".fake", ExposureSeconds = e.Seconds, FrameType = e.FrameType, Data = new RawBytes(new byte[] { 1, 2, 3 }) });
                _phase = "Idle"; await _pub.PublishAsync();
            });
            return CommandResult.Success();
        }, "fake expose");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ => Task.FromResult(CommandResult.Success()), "fake abort");
        await _pub.StartAsync();
    }

    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}
