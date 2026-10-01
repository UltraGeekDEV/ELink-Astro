using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>Makes a Mount usable as a Pointer: same sky, in J2000, with a notion of "settled on target".</summary>
public sealed class MountPointer : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id, _mountId;
    private readonly double _toleranceDegrees;
    private readonly RemoteState<MountState> _mount;
    private readonly CommandSet _commands;
    private readonly StatePublisher<PointerState> _publisher;
    private (double Ra, double Dec)? _target; // J2000

    public MountPointer(TypeSafeEVentNode node, string pointerId, string mountId, double toleranceDegrees = 0.05)
    {
        _node = node; _id = pointerId; _mountId = mountId; _toleranceDegrees = toleranceDegrees;
        _mount = new RemoteState<MountState>(node, EquipmentIds.State(DeviceKinds.Mount, mountId), EquipmentIds.GetState(DeviceKinds.Mount, mountId));
        _commands = new CommandSet(node);
        _publisher = new StatePublisher<PointerState>(node, PointerIds.State(pointerId), PointerIds.GetState(pointerId), Build);
    }

    public async Task StartAsync()
    {
        await _mount.StartAsync();
        _mount.Changed += __ => { _ = _publisher.PublishAsync(); };
        await _commands.AddAsync<SkyTarget, CommandResult>(PointerIds.Goto(_id), GotoAsync, $"slew the mount {_mountId} to a sky position and track it");
        await _commands.AddAsync<NOTESVoid, CommandResult>(PointerIds.Abort(_id), _ =>
            Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, _mountId, "Abort"), NOTESVoid.Void), "stop the slew");
        await _publisher.StartAsync();
    }

    private async Task<CommandResult> GotoAsync(SkyTarget target)
    {
        var result = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, _mountId, "Goto"), target);
        if (!result.Ok.Value) return result;
        _target = ToJ2000(target.RaHours.Value, target.DecDegrees.Value, target.Epoch.Text);
        // Publish before answering: whoever waits for OnTarget after this call must not see the previous target's state.
        await _publisher.PublishAsync();
        return result;
    }

    private static (double Ra, double Dec) ToJ2000(double ra, double dec, string epoch) =>
        epoch == "JNow" ? Precession.DateToJ2000(ra, dec, DateTime.UtcNow) : (ra, dec);

    private PointerState Build()
    {
        var m = _mount.Latest;
        var s = new PointerState();
        if (m is null || !m.Connected.Value) { s.Phase = "Disconnected"; return s; }
        var (ra, dec) = ToJ2000(m.RaHours.Value, m.DecDegrees.Value, m.Epoch.Text);
        s.RaHours = ra; s.DecDegrees = dec;
        s.Message = m.Message.Text;
        bool slewing = m.Phase.Text is "Slewing" or "Parking";
        s.OnTarget = _target is { } t && !slewing && m.Phase.Text is "Tracking" or "Idle"
                     && Sky.SeparationDegrees(ra, dec, t.Ra, t.Dec) <= _toleranceDegrees;
        s.Phase = m.Phase.Text switch
        {
            "Slewing" or "Parking" => "Slewing",
            "Parked" => "Parked",
            "Error" => "Error",
            _ => s.OnTarget ? "OnTarget" : "Idle",
        };
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Dispose(); _publisher.Dispose(); _mount.Dispose();
        await Task.CompletedTask;
    }
}
