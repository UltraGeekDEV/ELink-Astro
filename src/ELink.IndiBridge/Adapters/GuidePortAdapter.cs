using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

/// <summary>Any INDI device with the guider interface (a mount's pulse guiding, a camera's ST4 port) through the
/// standard TELESCOPE_TIMED_GUIDE_NS / TELESCOPE_TIMED_GUIDE_WE properties, as Ekos and PHD2's INDI driver use them.</summary>
public sealed class GuidePortAdapter(AdapterContext ctx) : IndiDeviceAdapter<GuidePortState>(ctx)
{
    public override string Kind => DeviceKinds.GuidePort;

    protected override GuidePortState BuildState()
    {
        var s = new GuidePortState { Connected = Connected };
        if (!Connected) return s;
        s.PulsingNorthSouth = P("TELESCOPE_TIMED_GUIDE_NS")?.State == IndiState.Busy;
        s.PulsingEastWest = P("TELESCOPE_TIMED_GUIDE_WE")?.State == IndiState.Busy;
        return s;
    }

    protected override Task RegisterCommandsAsync() =>
        RegisterCommandAsync<GuidePulse, CommandResult>("Pulse", PulseAsync, "play one timed guide pulse; answers when it is over");

    private async Task<CommandResult> PulseAsync(GuidePulse pulse)
    {
        int ms = pulse.Milliseconds.Value;
        if (ms < 0 || ms > 60000) return CommandResult.Fail("pulse length must be 0..60000 ms");
        var (property, element, other) = pulse.Direction.Text switch
        {
            "North" => ("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", "TIMED_GUIDE_S"),
            "South" => ("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S", "TIMED_GUIDE_N"),
            "West" => ("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W", "TIMED_GUIDE_E"),
            "East" => ("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", "TIMED_GUIDE_W"),
            _ => ("", "", ""),
        };
        if (property == "") return CommandResult.Fail("direction must be North, South, East or West");
        if (Need(property, element) is { } missing) return missing;
        if (ms == 0) return CommandResult.Success();
        var sent = await Send(() => Client.SetNumbersAsync(Device, property, new[] { (element, (double)ms), (other, 0.0) }));
        if (!sent.Ok.Value) return sent;
        // the pulse plays for its length; then the driver turns the property back from Busy
        await Task.Delay(ms);
        try { await Client.WaitForAsync(Device, property, p => p.State != IndiState.Busy, TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return CommandResult.Fail($"{Device} did not finish the {pulse.Direction.Text} pulse"); }
        return P(property)?.State == IndiState.Alert ? CommandResult.Fail($"{Device} reported an error on the {pulse.Direction.Text} pulse") : CommandResult.Success();
    }
}
