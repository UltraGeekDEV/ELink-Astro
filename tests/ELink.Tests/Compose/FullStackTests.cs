using System.Net;
using System.Net.Sockets;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Compose;

/// <summary>INDI simulators -> bridge node -> composition host node -> consumer node: three EVent nodes that know each other
/// only through IDs and contract types. A smart scope built from a simulated mount, camera and filter wheel runs an Observe.</summary>
public class FullStackTests : IAsyncLifetime
{
    // its own simulator server: the scope's slew and filter checks must not inherit other tests' mount and wheel state
    private IndiServerProcess _server = null!;
    private IndiServerProcess server => _server;
    public async Task InitializeAsync()
    {
        _server = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_ccd", "indi_simulator_wheel");
        Assert.True(await _server.WaitListeningAsync());
    }
    public Task DisposeAsync() { _server.Dispose(); return Task.CompletedTask; }

    private static int FreePort() => ELink.Testing.TestPorts.Next();

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    [Fact]
    public async Task SmartScopeFromSimulatorsObserves()
    {
        int bridgePort = FreePort();
        using var bridge = ElinkNode.Create("FS-Bridge", bridgePort);
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", server.Port);
        await link.StartAsync();

        using var hostNode = ElinkNode.Create("FS-Host", FreePort());
        Assert.Equal(MergeResult.Connected, await ElinkNode.JoinAsync(hostNode, "127.0.0.1", bridgePort));
        await using var host = new CompositionHost(hostNode);
        await host.StartAsync();

        using var ui = ElinkNode.Create("FS-Consumer", FreePort());
        Assert.Equal(MergeResult.Connected, await ElinkNode.JoinAsync(ui, "127.0.0.1", bridgePort));

        // wait for the equipment, connect it
        Assert.True(await Eventually(() =>
        {
            var all = ui.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void).GetAwaiter().GetResult()!.SelectMany(l => l.Devices).ToList();
            return new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("FilterWheel", "Filter_Simulator") }
                .All(w => all.Any(d => d.Kind.Text == w.Item1 && d.Id.Text == w.Item2));
        }));
        foreach (var (k, i) in new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("FilterWheel", "Filter_Simulator") })
            Assert.True((await Commands.CallAsync(ui, EquipmentIds.Command(k, i, "Connect"), (BinaryConvertibleBool)true)).Ok.Value);

        // earlier tests may have left the shared simulator mount parked
        await Commands.CallAsync(ui, EquipmentIds.Command("Mount", "Telescope_Simulator", "Park"), (BinaryConvertibleBool)false);
        var mountState = new RemoteState<MountState>(ui, EquipmentIds.State("Mount", "Telescope_Simulator"), EquipmentIds.GetState("Mount", "Telescope_Simulator"));
        await mountState.StartAsync();
        await mountState.WaitAsync(m => m.Connected.Value && m.Phase.Text != "Parked" && m.Phase.Text != "Parking", TimeSpan.FromSeconds(30));

        // compose, all by EVent
        Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "mountP", MountId = "Telescope_Simulator" })).Ok.Value);
        Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "camS", CameraId = "CCD_Simulator", FilterWheelId = "Filter_Simulator" })).Ok.Value);
        var def = new ScopeDefinition { Id = "sim1", DisplayName = "Simulated scope" };
        def.Pointers.Add("mountP"); def.Shooters.Add(new ScopeShooterRef { Id = "camS" });
        Assert.True((await Commands.CallAsync(ui, ScopeIds.Define, def)).Ok.Value);

        ShotEvent? shot = null; ScopeState? state = null;
        await ui.HookEventAsync(ShooterIds.Shot("sim1"), (ShotEvent s) => shot = s);
        await ui.HookEventAsync(ScopeIds.State("sim1"), (ScopeState s) => state = s);

        var r = await Commands.CallAsync(ui, ScopeIds.Command("sim1", "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = 3.0, DecDegrees = 40.0, Epoch = "J2000" },
            Exposure = new ShooterExposure { Seconds = 1, Filter = "Blue" }, Count = 1, SlewTimeoutSeconds = 120,
        });
        Assert.True(r.Ok.Value, r.Error.Text);

        Assert.True(await Eventually(() => state is { Observing.Value: false, ShotsDone.Value: 1 }, 120000),
            $"phase {state?.Phase.Text} {state?.Message.Text} | mount {mountState.Latest?.Phase.Text} {mountState.Latest?.RaHours.Value:0.000} {mountState.Latest?.DecDegrees.Value:0.000} parked {mountState.Latest?.Parked.Value} tracking {mountState.Latest?.Tracking.Value} target {mountState.Latest?.TargetRaHours.Value:0.000} {mountState.Latest?.TargetDecDegrees.Value:0.000} {mountState.Latest?.Message.Text}");
        Assert.Equal("", state!.Message.Text);
        Assert.True(await Eventually(() => shot is not null));
        Assert.StartsWith("SIMPLE", System.Text.Encoding.ASCII.GetString(shot!.Data.Data, 0, 6));
        Assert.Equal("Blue", shot.Filter.Text);
        // J2000 pointing at the time of the frame (the mount speaks JNow, converted back): where the mount really is, which
        // for the simulator can be a few arcminutes from where it was sent (centring corrects that on real rigs)
        Assert.True(ELink.Core.Astro.Sky.SeparationDegrees(shot.PointingRaHours.Value, shot.PointingDecDegrees.Value, 3.0, 40.0) * 60 < 6,
            $"shot pointing {shot.PointingRaHours.Value} {shot.PointingDecDegrees.Value}");
    }
}
