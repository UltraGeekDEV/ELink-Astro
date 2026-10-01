using ELink.Contracts.Equipment;
using ELink.Contracts.Indi;
using ELink.Core;
using ELink.IndiBridge;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Indi;

/// <summary>The standard INDI focuser interface (what Ekos drives, so any EAF-capable INDI focuser works), against the focuser
/// simulator in each of its modes: all features, absolute only, relative only, timer (DC) only.</summary>
public class FocuserInterfaceTests : IAsyncLifetime
{
    private IndiServerProcess _indi = null!;
    private TypeSafeEVentNode _node = null!;
    private IndiServerLink _link = null!;
    private RemoteState<FocuserState> _state = null!;
    private const string Id = "Focuser_Simulator";

    public async Task InitializeAsync()
    {
        _indi = new IndiServerProcess(IndiServerProcess.FreePort(), "indi_simulator_focus");
        Assert.True(await _indi.WaitListeningAsync());
        _node = ElinkNode.Create("FI-" + Guid.NewGuid().ToString("N")[..6], IndiServerProcess.FreePort());
        var dir = new DeviceDirectory(_node); await dir.StartAsync();
        _link = new IndiServerLink(_node, dir, "fi", "127.0.0.1", _indi.Port);
        await _link.StartAsync();
        Assert.True(await Eventually(() => dir.Devices.Any(d => d.Id.Text == Id)));
        _state = new RemoteState<FocuserState>(_node, EquipmentIds.State(DeviceKinds.Focuser, Id), EquipmentIds.GetState(DeviceKinds.Focuser, Id));
        await _state.StartAsync();
    }

    /// <summary>Choose the simulator's mode (which interface it presents) and connect. The mode must be chosen before the
    /// first connection: changed later, the simulator leaves stale definitions behind.</summary>
    private async Task ConnectIn(string mode)
    {
        Assert.True(await Eventually(() => _link.IsConnected));
        if (mode != "All")
        {
            var req = new IndiSetRequest { Device = "Focuser Simulator", Property = "Mode" };
            req.Elements.Add(new IndiSetElement { Id = mode, Value = "On" });
            bool set = false;
            await Eventually(() => set = _node.CallFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set("fi"), req).GetAwaiter().GetResult()!.Single().Ok.Value);
            Assert.True(set, "could not set the simulator mode");
            await Task.Delay(300);
        }
        Assert.True((await Cmd("Connect", (BinaryConvertibleBool)true)).Ok.Value);
        await _state.WaitAsync(s => s.Connected.Value, TimeSpan.FromSeconds(20));
        await Task.Delay(500);      // let the driver define all its properties
    }

    public async Task DisposeAsync() { _state.Dispose(); await _link.DisposeAsync(); _node.Dispose(); _indi.Dispose(); }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    private Task<CommandResult> Cmd<T>(string command, T input) where T : IBinaryConvertible, new() =>
        Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, Id, command), input);

    private Task<FocuserState> Until(Func<FocuserState, bool> p, int seconds = 60) => _state.WaitAsync(p, TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task AFullFeaturedFocuserReportsAndDoesEverythingItHas()
    {
        await ConnectIn("All");
        var s = await Until(f => f.CanMoveAbsolute.Value, 20);
        Assert.True(s.CanMoveAbsolute.Value && s.CanMoveRelative.Value && s.HasBacklash.Value);
        Assert.False(s.CanSync.Value); Assert.False(s.CanReverse.Value);          // the simulator has neither
        Assert.Equal(100000, s.MaxPosition.Value);
        Assert.False(double.IsNaN(s.Temperature.Value));
        Assert.False(double.IsNaN(s.Speed.Value));

        Assert.True((await Cmd("MoveTo", (BinaryConvertibleInt32)42000)).Ok.Value);
        await Until(f => f.Position.Value == 42000 && !f.Moving.Value);
        Assert.True((await Cmd("MoveBy", (BinaryConvertibleInt32)(-2000))).Ok.Value);                 // relative, inward
        await Until(f => f.Position.Value == 40000 && !f.Moving.Value);
        Assert.True((await Cmd("MoveBy", (BinaryConvertibleInt32)1500)).Ok.Value);                    // outward
        await Until(f => f.Position.Value == 41500 && !f.Moving.Value);

        Assert.True((await Cmd("SetBacklash", new FocusBacklash { Enabled = true, Steps = 75 })).Ok.Value);
        await Until(f => f.BacklashEnabled.Value && f.BacklashSteps.Value == 75, 20);
        Assert.True((await Cmd("SetBacklash", new FocusBacklash { Enabled = false })).Ok.Value);
        await Until(f => !f.BacklashEnabled.Value, 20);

        Assert.True((await Cmd("SetMaxPosition", (BinaryConvertibleInt32)80000)).Ok.Value);
        await Until(f => f.MaxPosition.Value == 80000, 20);
        Assert.False((await Cmd("MoveTo", (BinaryConvertibleInt32)90000)).Ok.Value);                  // beyond the new travel

        // what the focuser does not have is refused with a reason, not sent
        var sync = await Cmd("Sync", (BinaryConvertibleInt32)1000);
        Assert.False(sync.Ok.Value); Assert.Contains("FOCUS_SYNC", sync.Error.Text);
        Assert.False((await Cmd("SetReverse", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.False((await Cmd("MoveTimed", new FocusTimedMove { Milliseconds = 300 })).Ok.Value);
    }

    [Fact]
    public async Task AnAbsoluteOnlyFocuserStillMovesByStepsThroughAbsoluteMoves()
    {
        await ConnectIn("Absolute");
        var s = await Until(f => f.CanMoveAbsolute.Value && !f.CanMoveRelative.Value, 20);
        int start = s.Position.Value;
        Assert.True((await Cmd("MoveBy", (BinaryConvertibleInt32)(-3000))).Ok.Value);
        await Until(f => f.Position.Value == start - 3000 && !f.Moving.Value);
    }

    [Fact]
    public async Task ARelativeOnlyFocuserMovesBySteps()
    {
        await ConnectIn("Relative");
        await Until(f => f.CanMoveRelative.Value && !f.CanMoveAbsolute.Value, 20);
        Assert.False((await Cmd("MoveTo", (BinaryConvertibleInt32)1000)).Ok.Value);                   // no absolute position
        Assert.True((await Cmd("MoveBy", (BinaryConvertibleInt32)500)).Ok.Value);
        Assert.True(await Eventually(() => _state.Latest is { Moving.Value: false }, 30000));
    }

    [Fact]
    public async Task ADcFocuserMovesForATime()
    {
        await ConnectIn("Timer");
        await Until(f => f.CanMoveTimed.Value && !f.CanMoveAbsolute.Value && !f.CanMoveRelative.Value, 20);
        Assert.False((await Cmd("MoveBy", (BinaryConvertibleInt32)100)).Ok.Value);                    // no steps at all: says to use MoveTimed
        Assert.True((await Cmd("MoveTimed", new FocusTimedMove { Outward = true, Milliseconds = 400 })).Ok.Value);
        Assert.True(await Eventually(() => _state.Latest is { Moving.Value: false }, 20000));
        Assert.False((await Cmd("MoveTimed", new FocusTimedMove { Milliseconds = 0 })).Ok.Value);
    }
}
