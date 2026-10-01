using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

public sealed class FocuserAdapter(AdapterContext ctx) : IndiDeviceAdapter<FocuserState>(ctx)
{
    public override string Kind => DeviceKinds.Focuser;

    protected override FocuserState BuildState()
    {
        var s = new FocuserState { Connected = Connected };
        if (!Connected) return s;
        var abs = P("ABS_FOCUS_POSITION");
        s.CanMoveAbsolute = abs is not null;
        if (abs is not null) s.Position = (int)abs.Number("FOCUS_ABSOLUTE_POSITION");
        if (P("FOCUS_MAX") is { } max) s.MaxPosition = (int)max.Number("FOCUS_MAX_VALUE");
        s.Moving = abs?.State == IndiState.Busy || P("REL_FOCUS_POSITION")?.State == IndiState.Busy;
        if (P("FOCUS_TEMPERATURE") is { } t && t.Elements.Length > 0) s.Temperature = t.Elements[0].AsNumber();
        if (abs?.State == IndiState.Alert) s.Message = "the focuser reported an error";
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("MoveTo", MoveToAsync, "move to an absolute position in steps");
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("MoveBy", MoveByAsync, "move by a number of steps; negative = inward");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Abort", _ => AbortAsync(), "stop the focuser");
    }

    private async Task<CommandResult> MoveToAsync(BinaryConvertibleInt32 position)
    {
        if (Need("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION") is { } missing) return missing;
        var max = P("FOCUS_MAX")?.Number("FOCUS_MAX_VALUE") ?? double.NaN;
        if (position.Value < 0 || (!double.IsNaN(max) && position.Value > max)) return CommandResult.Fail($"position out of range 0..{max}");
        return await Send(() => SetNumber("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION", position.Value));
    }

    private async Task<CommandResult> MoveByAsync(BinaryConvertibleInt32 steps)
    {
        if (steps.Value == 0) return CommandResult.Success();
        if (Need("REL_FOCUS_POSITION", "FOCUS_RELATIVE_POSITION") is { } missing) return missing;
        return await Send(async () =>
        {
            if (P("FOCUS_MOTION") is not null) await SetSwitch("FOCUS_MOTION", steps.Value < 0 ? "FOCUS_INWARD" : "FOCUS_OUTWARD");
            await SetNumber("REL_FOCUS_POSITION", "FOCUS_RELATIVE_POSITION", Math.Abs(steps.Value));
        });
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("FOCUS_ABORT_MOTION", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("FOCUS_ABORT_MOTION", "ABORT"));
    }
}
