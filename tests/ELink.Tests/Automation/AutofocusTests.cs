using System.Net;
using System.Net.Sockets;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Indi;
using ELink.Core;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

[Collection("indiserver")]
public class AutofocusTests(IndiServerFixture server)
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task FindsTheSimulatorsTrueFocus()
    {
        Assert.True(server.Available);
        int bridgePort = FreePort();
        using var bridge = ElinkNode.Create("AF-Bridge", bridgePort);
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", server.Port);
        await link.StartAsync();

        using var svcNode = ElinkNode.Create("AF-Service", FreePort());
        await ElinkNode.JoinAsync(svcNode, "127.0.0.1", bridgePort);
        await using var host = new CompositionHost(svcNode); await host.StartAsync();
        await using var af = new AutofocusService(svcNode); await af.StartAsync();

        using var ui = ElinkNode.Create("AF-Client", FreePort());
        await ElinkNode.JoinAsync(ui, "127.0.0.1", bridgePort);

        Assert.True(await Eventually(() =>
        {
            var all = ui.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void).GetAwaiter().GetResult()!.SelectMany(l => l.Devices).ToList();
            return new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("Focuser", "Focuser_Simulator") }
                .All(w => all.Any(d => d.Kind.Text == w.Item1 && d.Id.Text == w.Item2));
        }));
        async Task<CommandResult> Cmd<T>(string kind, string id, string cmd, T input) where T : IBinaryConvertible, new() =>
            await Commands.CallAsync(ui, EquipmentIds.Command(kind, id, cmd), input);
        foreach (var (k, i) in new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("Focuser", "Focuser_Simulator") })
            Assert.True((await Cmd(k, i, "Connect", (BinaryConvertibleBool)true)).Ok.Value);

        // a star field: give the simulated camera an optical system, point the mount at a rich area
        var info = new IndiSetRequest { Device = "CCD Simulator", Property = "SCOPE_INFO" };
        info.Elements.Add(new IndiSetElement { Id = "FOCAL_LENGTH", Value = "1000" }); info.Elements.Add(new IndiSetElement { Id = "APERTURE", Value = "200" });
        bool infoSet = false;
        await Eventually(() => infoSet = Assert.Single(ui.CallFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set("sim"), info).GetAwaiter().GetResult()!).Ok.Value);
        Assert.True(infoSet, "SCOPE_INFO never became settable");

        var mount = new RemoteState<MountState>(ui, EquipmentIds.State("Mount", "Telescope_Simulator"), EquipmentIds.GetState("Mount", "Telescope_Simulator"));
        await mount.StartAsync();
        await mount.WaitAsync(m => m.Connected.Value, TimeSpan.FromSeconds(30));
        await Cmd("Mount", "Telescope_Simulator", "Park", (BinaryConvertibleBool)false);
        await mount.WaitAsync(m => m.Phase.Text != "Parked" && m.Phase.Text != "Parking", TimeSpan.FromSeconds(30));
        Assert.True((await Cmd("Mount", "Telescope_Simulator", "Goto", new SkyTarget { RaHours = 5.6, DecDegrees = -1, Epoch = "JNow" })).Ok.Value);
        await mount.WaitAsync(m => m.Phase.Text == "Tracking" && Math.Abs(m.RaHours.Value - 5.6) < 0.01, TimeSpan.FromSeconds(180));

        Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "afcam", CameraId = "CCD_Simulator" })).Ok.Value);

        var focuser = new RemoteState<FocuserState>(ui, EquipmentIds.State("Focuser", "Focuser_Simulator"), EquipmentIds.GetState("Focuser", "Focuser_Simulator"));
        await focuser.StartAsync();
        await focuser.WaitAsync(f => f.Connected.Value && f.CanMoveAbsolute.Value, TimeSpan.FromSeconds(30));
        Assert.True((await Cmd("Focuser", "Focuser_Simulator", "MoveTo", (BinaryConvertibleInt32)50000)).Ok.Value);   // well out of focus
        await focuser.WaitAsync(f => f.Position.Value == 50000 && !f.Moving.Value, TimeSpan.FromSeconds(120));

        AutofocusState? last = null;
        await ui.HookEventAsync(AutofocusIds.State, (AutofocusState s) => last = s);
        var run = await Commands.CallAsync(ui, AutofocusIds.Run, new AutofocusRequest { ShooterId = "afcam", FocuserId = "Focuser_Simulator", ExposureSeconds = 2, StepSize = 3000, Samples = 7 });
        Assert.True(run.Ok.Value, run.Error.Text);
        Assert.False((await Commands.CallAsync(ui, AutofocusIds.Run, new AutofocusRequest { ShooterId = "afcam", FocuserId = "Focuser_Simulator" })).Ok.Value);   // one run at a time

        Assert.True(await Eventually(() => last is { Phase.Text: "Done" or "Error" or "Aborted" }, 480000), $"phase {last?.Phase.Text} {last?.Message.Text}");
        Assert.Equal("Done", last!.Phase.Text);
        Assert.InRange(last.BestPosition.Value, 34500, 39000);          // the simulator's true focus is 36700
        Assert.True(last.BestHfr.Value < 2.3, $"hfr {last.BestHfr.Value}");
        Assert.True(last.Points.Count >= 8);
    }

    [Fact]
    public async Task RejectsBadRequestsAndMissingFocuser()
    {
        using var node = ElinkNode.Create("AF-Solo", FreePort());
        await using var af = new AutofocusService(node); await af.StartAsync();
        Assert.False((await Commands.CallAsync(node, AutofocusIds.Run, new AutofocusRequest())).Ok.Value);
        Assert.False((await Commands.CallAsync(node, AutofocusIds.Run, new AutofocusRequest { ShooterId = "s", FocuserId = "f", StepSize = 0 })).Ok.Value);
        AutofocusState? last = null;
        await node.HookEventAsync(AutofocusIds.State, (AutofocusState s) => last = s);
        Assert.True((await Commands.CallAsync(node, AutofocusIds.Run, new AutofocusRequest { ShooterId = "s", FocuserId = "ghost" })).Ok.Value);
        Assert.True(await Eventually(() => last is { Phase.Text: "Error" }, 15000));
        Assert.Contains("ghost", last!.Message.Text);
    }
}
