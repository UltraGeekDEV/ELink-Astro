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

/// <summary>The whole chain for real: INDI telescope + CCD + rotator simulators -> bridge node -> composition / mosaic / storage
/// node -> a controlling node that only knows EVent IDs. The simulated mount really slews between the poses, the rotator turns
/// between passes, and the frames land on disk.</summary>
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
    public async Task SimulatedMountPaintsAVirtualFovWithRotatedPassesAndTheFramesLandOnDisk()
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
                return new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("Rotator", "Rotator_Simulator") }.All(w => all.Any(d => d.Kind.Text == w.Item1 && d.Id.Text == w.Item2));
            }));
            foreach (var (k, i) in new[] { ("Mount", "Telescope_Simulator"), ("Camera", "CCD_Simulator"), ("Rotator", "Rotator_Simulator") })
                Assert.True((await Commands.CallAsync(ui, EquipmentIds.Command(k, i, "Connect"), (BinaryConvertibleBool)true)).Ok.Value);
            var mount = new RemoteState<MountState>(ui, EquipmentIds.State("Mount", "Telescope_Simulator"), EquipmentIds.GetState("Mount", "Telescope_Simulator"));
            await mount.StartAsync();
            await mount.WaitAsync(m => m.Connected.Value, TimeSpan.FromSeconds(30));
            await Commands.CallAsync(ui, EquipmentIds.Command("Mount", "Telescope_Simulator", "Park"), (BinaryConvertibleBool)false);
            await mount.WaitAsync(m => m.Phase.Text != "Parked" && m.Phase.Text != "Parking", TimeSpan.FromSeconds(30));
            var rotator = new RemoteState<RotatorState>(ui, EquipmentIds.State("Rotator", "Rotator_Simulator"), EquipmentIds.GetState("Rotator", "Rotator_Simulator"));
            await rotator.StartAsync();

            Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" })).Ok.Value);
            Assert.True((await Commands.CallAsync(ui, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "cam", CameraId = "CCD_Simulator" })).Ok.Value);
            var def = new ScopeDefinition { Id = "main" }; def.Pointers.Add("eq"); def.Shooters.Add(new ScopeShooterRef { Id = "cam" });
            Assert.True((await Commands.CallAsync(ui, ScopeIds.Define, def)).Ok.Value);
            Assert.True((await Commands.CallAsync(ui, StorageIds.Watch, new StorageWatch { ShooterId = "main" })).Ok.Value);

            MosaicState? state = null;
            await ui.HookEventAsync(MosaicIds.State, (MosaicState s) => state = s);
            var request = new MosaicRequest
            {
                Label = "Field", ScopeId = "main", RotatorId = "Rotator_Simulator", TargetSeconds = 2,
                Center = new SkyTarget { RaHours = 5.0, DecDegrees = 30, Epoch = "J2000" },
                FovWidthDegrees = 0.4, FovHeightDegrees = 0.3, PositionAngleDegrees = 10, StepoverDegrees = 0.05,
                Exposure = new ShooterExposure { Seconds = 1 }, SlewTimeoutSeconds = 120,
            };
            request.Frames.Add(new MosaicFrame { WidthDegrees = 0.2, HeightDegrees = 0.2 });
            request.FieldRotations.Add(0); request.FieldRotations.Add(90);

            var preview = Assert.Single((await ui.CallFunctionAsync<MosaicRequest, MosaicPreview>(MosaicIds.Plan, request))!);
            Assert.Equal("", preview.Error.Text);
            Assert.True(preview.EstimatedVisits.Value is > 5 and < 80, $"estimated {preview.EstimatedVisits.Value}");

            var started = await Commands.CallAsync(ui, MosaicIds.Start, request);
            Assert.True(started.Ok.Value, started.Error.Text);
            Assert.True(await Eventually(() => state is { Phase.Text: "Done" or "Error" or "Aborted" }, 480000), $"{state?.Phase.Text} {state?.Message.Text} visits {state?.Visits.Value}");
            Assert.Equal("Done", state!.Phase.Text);
            int visits = state.Visits.Value;
            Assert.True(visits >= 8, $"{visits} visits");
            Assert.True(state.MinSeconds.Value >= 2 - 1e-6, $"every spot must have 2 s: min {state.MinSeconds.Value}");
            Assert.Equal(2, state.Passes.Value);                                                  // one light pass per field rotation

            // single shots, one frame per visit, saved with their object name and pointing; they spread over the area and a bit beyond
            Assert.True(await Eventually(() => storage.FramesSaved == visits, 120000), $"saved {storage.FramesSaved} of {visits}");
            var files = Directory.GetFiles(saveDir, "*.fits", SearchOption.AllDirectories);
            Assert.Equal(visits, files.Length);
            Assert.All(files, f => Assert.StartsWith("Field_Light_1s_", Path.GetFileName(f)));
            var poses = files.Select(f => FitsImage.Parse(File.ReadAllBytes(f))).Select(i => Gnomonic.FromSky(5.0, 30, i.GetDouble("RA") / 15, i.GetDouble("DEC"))).ToList();
            Assert.InRange(poses.Select(p => Math.Round(p.EastDegrees, 2)).Distinct().Count(), 4, 1000);
            Assert.True(poses.All(p => Math.Abs(p.EastDegrees) < 0.2 + 0.2 && Math.Abs(p.NorthDegrees) < 0.15 + 0.2), "poses stay within one frame's reach of the area");

            // the rotator really turned: it ended at the last pass's angle
            Assert.True(await Eventually(() => rotator.Latest is { Moving.Value: false }));
            double angle = rotator.Latest!.AngleDegrees.Value;
            Assert.True(Math.Abs(angle) < 0.6 || Math.Abs(angle - 90) < 0.6, $"rotator at {angle}");
        }
        finally { try { Directory.Delete(saveDir, true); } catch { } }
    }
}
