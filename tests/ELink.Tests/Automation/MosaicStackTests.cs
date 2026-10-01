using System.Net;
using System.Net.Sockets;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Imaging;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The whole chain for real: INDI telescope + CCD simulators -> bridge node -> composition / mosaic / storage node -> a
/// controlling node that only knows EVent IDs. The simulated mount really slews between the panels.</summary>
[Collection("indiserver")]
public class MosaicStackTests(IndiServerFixture server)
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task SimulatedMountScansAMosaicOfSingleShotsAndTheFramesLandOnDisk()
    {
        Assert.True(server.Available);
        int bridgePort = FreePort();
        string saveDir = Path.Combine(Path.GetTempPath(), "elink-mosaic-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bridge = ElinkNode.Create("MS-Bridge", bridgePort);
            var dir = new DeviceDirectory(bridge); await dir.StartAsync();
            await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", server.Port);
            await link.StartAsync();

            using var work = ElinkNode.Create("MS-Work", FreePort());
            await ElinkNode.JoinAsync(work, "127.0.0.1", bridgePort);
            await using var compose = new CompositionHost(work); await compose.StartAsync();
            await using var mosaic = new MosaicService(work); await mosaic.StartAsync();
            await using var storage = new StorageService(work, saveDir); await storage.StartAsync();

            using var ui = ElinkNode.Create("MS-Control", FreePort());
            await ElinkNode.JoinAsync(ui, "127.0.0.1", bridgePort);

            Assert.True(await Eventually(() =>
            {
                var all = ui.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void).GetAwaiter().GetResult()!.SelectMany(l => l.Devices).ToList();
                return all.Any(d => d.Kind.Text == "Mount" && d.Id.Text == "Telescope_Simulator") && all.Any(d => d.Kind.Text == "Camera" && d.Id.Text == "CCD_Simulator");
            }));
            foreach (var (k, i) in new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator") })
                Assert.True((await Commands.CallAsync(ui, EquipmentIds.Command(k, i, "Connect"), (BinaryConvertibleBool)true)).Ok.Value);
            var mount = new RemoteState<MountState>(ui, EquipmentIds.State("Mount", "Telescope_Simulator"), EquipmentIds.GetState("Mount", "Telescope_Simulator"));
            await mount.StartAsync();
            await mount.WaitAsync(m => m.Connected.Value, TimeSpan.FromSeconds(30));
            await Commands.CallAsync(ui, EquipmentIds.Command("Mount", "Telescope_Simulator", "Park"), (BinaryConvertibleBool)false);
            await mount.WaitAsync(m => m.Phase.Text != "Parked" && m.Phase.Text != "Parking", TimeSpan.FromSeconds(30));

            Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" })).Ok.Value);
            Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "cam", CameraId = "CCD_Simulator" })).Ok.Value);
            var def = new ScopeDefinition { Id = "main" }; def.Pointers.Add("eq"); def.Shooters.Add(new ScopeShooterRef { Id = "cam" });
            Assert.True((await Commands.CallAsync(ui, ScopeIds.Define, def)).Ok.Value);
            Assert.True((await Commands.CallAsync(ui, StorageIds.Watch, new StorageWatch { ShooterId = "main" })).Ok.Value);

            MosaicState? state = null;
            await ui.HookEventAsync(MosaicIds.State, (MosaicState s) => state = s);
            var request = new MosaicRequest
            {
                Label = "Field", ScopeId = "main", Passes = 2,
                Center = new SkyTarget { RaHours = 5.0, DecDegrees = 30, Epoch = "J2000" },
                FovWidthDegrees = 1.0, FovHeightDegrees = 0.5, FrameWidthDegrees = 0.5, FrameHeightDegrees = 0.5, Overlap = 0.2,
                Exposure = new ShooterExposure { Seconds = 1 }, SlewTimeoutSeconds = 120,
            };
            var layout = Assert.Single((await ui.CallFunctionAsync<MosaicRequest, MosaicLayout>(MosaicIds.Plan, request))!);
            Assert.Equal((3, 1), (layout.Cols.Value, layout.Rows.Value));

            var started = await Commands.CallAsync(ui, MosaicIds.Start, request);
            Assert.True(started.Ok.Value, started.Error.Text);
            Assert.True(await Eventually(() => state is { Phase.Text: "Done" or "Error" or "Aborted" }, 300000), $"{state?.Phase.Text} {state?.Message.Text} visits {state?.VisitsDone.Value}");
            Assert.Equal("Done", state!.Phase.Text);
            Assert.Equal(6, state.VisitsDone.Value);                                           // 3 panels x 2 passes, single shots
            Assert.All(state.Layout.Panels, p => Assert.Equal(2, p.Frames.Value));

            // the frames are on disk, one per visit, named per panel, each stamped with where the mount was pointing
            Assert.True(await Eventually(() => storage.FramesSaved == 6, 60000), $"saved {storage.FramesSaved}");
            var files = Directory.GetFiles(saveDir, "*.fits", SearchOption.AllDirectories).Order().ToArray();
            Assert.Equal(6, files.Length);
            Assert.Equal(3, files.Select(f => Path.GetFileName(f).Split("_Light")[0]).Distinct().Count());     // Field_r1c1, Field_r1c2, Field_r1c3
            var ras = files.Select(f => FitsImage.Parse(File.ReadAllBytes(f))).Select(i => i.GetDouble("RA") / 15).ToArray();
            Assert.Equal(3, ras.Select(r => Math.Round(r, 2)).Distinct().Count());          // three different pointings
            Assert.InRange(ras.Max() - ras.Min(), 0.75 / 15 / Math.Cos(30 * Math.PI / 180), 1.2 / 15 / Math.Cos(30 * Math.PI / 180));   // the mosaic's width (about 0.8 deg between outer centres)
        }
        finally { try { Directory.Delete(saveDir, true); } catch { } }
    }
}
