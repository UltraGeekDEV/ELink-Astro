using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

public sealed class RotatorAdapter(AdapterContext ctx) : IndiDeviceAdapter<RotatorState>(ctx)
{
    public override string Kind => DeviceKinds.Rotator;

    protected override RotatorState BuildState()
    {
        var s = new RotatorState { Connected = Connected };
        if (!Connected) return s;
        if (P("ABS_ROTATOR_ANGLE") is { } a) { s.AngleDegrees = a.Number("ANGLE"); s.Moving = a.State == IndiState.Busy; }
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleDouble, CommandResult>("MoveTo", MoveToAsync, "rotate to an absolute angle in degrees");
        await RegisterCommandAsync<BinaryConvertibleDouble, CommandResult>("Sync", SyncAsync, "declare the current angle to be this value");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Abort", _ => AbortAsync(), "stop rotating");
    }

    private async Task<CommandResult> MoveToAsync(BinaryConvertibleDouble angle)
    {
        if (Need("ABS_ROTATOR_ANGLE", "ANGLE") is { } missing) return missing;
        if (double.IsNaN(angle.Value)) return CommandResult.Fail("invalid angle");
        return await Send(() => SetNumber("ABS_ROTATOR_ANGLE", "ANGLE", angle.Value));
    }

    private async Task<CommandResult> SyncAsync(BinaryConvertibleDouble angle)
    {
        if (Need("SYNC_ROTATOR_ANGLE", "ANGLE") is { } missing) return missing;
        return await Send(() => SetNumber("SYNC_ROTATOR_ANGLE", "ANGLE", angle.Value));
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("ROTATOR_ABORT_MOTION", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("ROTATOR_ABORT_MOTION", "ABORT"));
    }
}
