using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

/// <summary>Any INDI focuser (ZWO EAF, Moonlite, the simulator, ...) through the standard INDI focuser interface, the same
/// properties Ekos drives: ABS_FOCUS_POSITION, REL_FOCUS_POSITION + FOCUS_MOTION, FOCUS_TIMER, FOCUS_ABORT_MOTION,
/// FOCUS_SYNC, FOCUS_REVERSE_MOTION, FOCUS_BACKLASH_TOGGLE / _STEPS, FOCUS_MAX, FOCUS_SPEED, FOCUS_TEMPERATURE.
/// What a focuser can do is detected from which of those it defines.</summary>
public sealed class FocuserAdapter(AdapterContext ctx) : IndiDeviceAdapter<FocuserState>(ctx)
{
    public override string Kind => DeviceKinds.Focuser;

    protected override FocuserState BuildState()
    {
        var s = new FocuserState { Connected = Connected };
        if (!Connected) return s;
        var abs = P("ABS_FOCUS_POSITION");
        var rel = P("REL_FOCUS_POSITION");
        var timer = P("FOCUS_TIMER");
        s.CanMoveAbsolute = abs is not null;
        s.CanMoveRelative = rel is not null;
        s.CanMoveTimed = timer is not null && P("FOCUS_MOTION") is not null;
        s.CanAbort = P("FOCUS_ABORT_MOTION") is not null;
        s.CanSync = P("FOCUS_SYNC") is not null;
        if (abs is not null) s.Position = (int)Math.Round(abs.Number("FOCUS_ABSOLUTE_POSITION"));
        // max travel: FOCUS_MAX if the driver has it, else the limit of the absolute position
        if (P("FOCUS_MAX") is { } max) s.MaxPosition = (int)max.Number("FOCUS_MAX_VALUE");
        else if (abs?["FOCUS_ABSOLUTE_POSITION"] is { } absElement) s.MaxPosition = (int)absElement.Max;
        s.Moving = abs?.State == IndiState.Busy || rel?.State == IndiState.Busy || timer?.State == IndiState.Busy;
        if (P("FOCUS_TEMPERATURE") is { } t && t.Elements.Length > 0) s.Temperature = t.Elements[0].AsNumber();
        if (P("FOCUS_REVERSE_MOTION") is { } rev) { s.CanReverse = true; s.Reversed = rev.Switch("INDI_ENABLED"); }
        if (P("FOCUS_BACKLASH_STEPS") is { } bl)
        {
            s.HasBacklash = true;
            s.BacklashSteps = (int)bl.Number("FOCUS_BACKLASH_VALUE");
            s.BacklashEnabled = P("FOCUS_BACKLASH_TOGGLE")?.Switch("INDI_ENABLED") ?? s.BacklashSteps.Value != 0;
        }
        if (P("FOCUS_SPEED") is { } sp && sp["FOCUS_SPEED_VALUE"] is { } spe) { s.Speed = spe.AsNumber(); s.SpeedMax = spe.Max; }
        if (abs?.State == IndiState.Alert || rel?.State == IndiState.Alert) s.Message = "the focuser reported an error";
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("MoveTo", MoveToAsync, "move to an absolute position in steps");
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("MoveBy", MoveByAsync, "move by a number of steps; negative = inward");
        await RegisterCommandAsync<FocusTimedMove, CommandResult>("MoveTimed", MoveTimedAsync, "move in a direction for a time (DC focusers)");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Abort", _ => AbortAsync(), "stop the focuser");
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("Sync", SyncAsync, "declare the current position to be this many steps, without moving");
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("SetReverse", ReverseAsync, "reverse the direction of motion");
        await RegisterCommandAsync<FocusBacklash, CommandResult>("SetBacklash", BacklashAsync, "turn backlash compensation on or off and set its steps");
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("SetMaxPosition", MaxAsync, "set the maximum travel in steps");
        await RegisterCommandAsync<BinaryConvertibleDouble, CommandResult>("SetSpeed", SpeedAsync, "set the motion speed (driver units)");
    }

    private int? MaxTravel()
    {
        if (P("FOCUS_MAX") is { } m) return (int)m.Number("FOCUS_MAX_VALUE");
        return P("ABS_FOCUS_POSITION")?["FOCUS_ABSOLUTE_POSITION"] is { } e && e.Max > 0 ? (int)e.Max : null;
    }

    private async Task<CommandResult> MoveToAsync(BinaryConvertibleInt32 position)
    {
        if (Need("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION") is { } missing) return missing;
        if (position.Value < 0 || (MaxTravel() is int max && position.Value > max)) return CommandResult.Fail($"position out of range 0..{MaxTravel()}");
        return await Send(() => SetNumber("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION", position.Value));
    }

    private async Task<CommandResult> MoveByAsync(BinaryConvertibleInt32 steps)
    {
        if (steps.Value == 0) return CommandResult.Success();
        if (P("REL_FOCUS_POSITION")?.Has("FOCUS_RELATIVE_POSITION") == true)
            return await Send(async () =>
            {
                if (P("FOCUS_MOTION") is not null) await SetSwitch("FOCUS_MOTION", steps.Value < 0 ? "FOCUS_INWARD" : "FOCUS_OUTWARD");
                await SetNumber("REL_FOCUS_POSITION", "FOCUS_RELATIVE_POSITION", Math.Abs(steps.Value));
            });
        // absolute-only focusers: a relative move is an absolute one from here
        if (P("ABS_FOCUS_POSITION") is { } abs)
        {
            int target = (int)Math.Round(abs.Number("FOCUS_ABSOLUTE_POSITION")) + steps.Value;
            return await MoveToAsync(Math.Clamp(target, 0, MaxTravel() ?? int.MaxValue));
        }
        return CommandResult.Fail($"{Device} cannot move by steps (try MoveTimed)");
    }

    private async Task<CommandResult> MoveTimedAsync(FocusTimedMove move)
    {
        if (Need("FOCUS_TIMER", "FOCUS_TIMER_VALUE") is { } missing) return missing;
        if (Need("FOCUS_MOTION") is { } noMotion) return noMotion;
        if (move.Milliseconds.Value <= 0) return CommandResult.Fail("the duration must be positive");
        return await Send(async () =>
        {
            await SetSwitch("FOCUS_MOTION", move.Outward.Value ? "FOCUS_OUTWARD" : "FOCUS_INWARD");
            await SetNumber("FOCUS_TIMER", "FOCUS_TIMER_VALUE", move.Milliseconds.Value);
        });
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("FOCUS_ABORT_MOTION", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("FOCUS_ABORT_MOTION", "ABORT"));
    }

    private async Task<CommandResult> SyncAsync(BinaryConvertibleInt32 position)
    {
        if (Need("FOCUS_SYNC", "FOCUS_SYNC_VALUE") is { } missing) return missing;
        if (position.Value < 0) return CommandResult.Fail("position cannot be negative");
        return await Send(() => SetNumber("FOCUS_SYNC", "FOCUS_SYNC_VALUE", position.Value));
    }

    private async Task<CommandResult> ReverseAsync(BinaryConvertibleBool on)
    {
        if (Need("FOCUS_REVERSE_MOTION") is { } missing) return missing;
        return await Send(() => SetSwitch("FOCUS_REVERSE_MOTION", on.Value ? "INDI_ENABLED" : "INDI_DISABLED"));
    }

    private async Task<CommandResult> BacklashAsync(FocusBacklash b)
    {
        if (Need("FOCUS_BACKLASH_STEPS", "FOCUS_BACKLASH_VALUE") is { } missing) return missing;
        if (b.Steps.Value < 0) return CommandResult.Fail("backlash steps cannot be negative");
        return await Send(async () =>
        {
            // as INDI drivers expect: enable first, then the value (some drivers only accept steps while enabled)
            if (P("FOCUS_BACKLASH_TOGGLE") is not null) await SetSwitch("FOCUS_BACKLASH_TOGGLE", b.Enabled.Value ? "INDI_ENABLED" : "INDI_DISABLED");
            if (b.Enabled.Value) await SetNumber("FOCUS_BACKLASH_STEPS", "FOCUS_BACKLASH_VALUE", b.Steps.Value);
        });
    }

    private async Task<CommandResult> MaxAsync(BinaryConvertibleInt32 max)
    {
        if (Need("FOCUS_MAX", "FOCUS_MAX_VALUE") is { } missing) return missing;
        if (max.Value <= 0) return CommandResult.Fail("the maximum must be positive");
        return await Send(() => SetNumber("FOCUS_MAX", "FOCUS_MAX_VALUE", max.Value));
    }

    private async Task<CommandResult> SpeedAsync(BinaryConvertibleDouble speed)
    {
        if (Need("FOCUS_SPEED", "FOCUS_SPEED_VALUE") is { } missing) return missing;
        var e = P("FOCUS_SPEED")!["FOCUS_SPEED_VALUE"]!;
        if (speed.Value < e.Min || (e.Max > e.Min && speed.Value > e.Max)) return CommandResult.Fail($"speed out of range {e.Min}..{e.Max}");
        return await Send(() => SetNumber("FOCUS_SPEED", "FOCUS_SPEED_VALUE", speed.Value));
    }
}
