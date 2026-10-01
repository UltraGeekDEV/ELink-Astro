using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

public sealed class FilterWheelAdapter(AdapterContext ctx) : IndiDeviceAdapter<FilterWheelState>(ctx)
{
    public override string Kind => DeviceKinds.FilterWheel;

    protected override FilterWheelState BuildState()
    {
        var s = new FilterWheelState { Connected = Connected };
        if (!Connected) return s;
        var slot = P("FILTER_SLOT");
        if (slot is not null) { s.Slot = (int)slot.Number("FILTER_SLOT_VALUE"); s.Moving = slot.State == IndiState.Busy; }
        if (P("FILTER_NAME") is { } names)
            foreach (var e in names.Elements) s.FilterNames.Add(e.Value);
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleInt32, CommandResult>("SelectSlot", SelectSlotAsync, "move to slot n (1-based)");
        await RegisterCommandAsync<BinaryConvertibleString, CommandResult>("SelectFilter", SelectFilterAsync, "move to the slot with this filter name");
    }

    private async Task<CommandResult> SelectSlotAsync(BinaryConvertibleInt32 slot)
    {
        var p = P("FILTER_SLOT");
        if (p is null) return CommandResult.Fail($"{Device} has no FILTER_SLOT");
        var e = p["FILTER_SLOT_VALUE"];
        if (e is null) return CommandResult.Fail("no FILTER_SLOT_VALUE");
        if (slot.Value < e.Min || slot.Value > e.Max) return CommandResult.Fail($"slot out of range {e.Min}..{e.Max}");
        return await Send(() => SetNumber("FILTER_SLOT", "FILTER_SLOT_VALUE", slot.Value));
    }

    private async Task<CommandResult> SelectFilterAsync(BinaryConvertibleString name)
    {
        var names = P("FILTER_NAME");
        if (names is null) return CommandResult.Fail($"{Device} has no FILTER_NAME");
        int i = 0;
        foreach (var e in names.Elements)
        {
            i++;
            if (string.Equals(e.Value, name.Text, StringComparison.OrdinalIgnoreCase)) return await SelectSlotAsync(i);
        }
        return CommandResult.Fail($"no filter named {name.Text}");
    }
}
