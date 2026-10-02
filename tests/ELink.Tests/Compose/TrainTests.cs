using System.Collections.Concurrent;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests.Compose;

public class TrainCompositionTests
{
    private static ImagingTrainDefinition Train(string id, double fl, params (string Camera, string Role)[] cams)
    {
        var t = new ImagingTrainDefinition { Id = id, Label = id, FocalLengthMm = fl, ApertureMm = 80, FilterWheelId = "wheel", FocuserId = "foc" };
        foreach (var (c, r) in cams) t.Cameras.Add(new TrainCamera { CameraId = c, Role = r });
        return t;
    }

    [Fact]
    public async Task TrainsAndScopeGuidingAreValidatedSavedAndRestored()
    {
        string file = Path.Combine(Path.GetTempPath(), "elink-trains-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var node = ElinkNode.Create("TC-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
            await using (var host = new CompositionHost(node, file))
            {
                await host.StartAsync();
                Assert.False((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("bad", 400))).Ok.Value);                                  // no camera
                Assert.False((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("bad", 400, ("c1", "Viewing")))).Ok.Value);              // no such role
                Assert.False((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("bad", 400, ("c1", "Imaging"), ("c1", "Guiding")))).Ok.Value);
                // two OTAs and a guide scope on one mount; the main OTA also has an off-axis guider
                Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("main", 800, ("cam1", "Imaging"), ("oag", "Guiding")))).Ok.Value);
                Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("wide", 250, ("cam2", "Imaging")))).Ok.Value);
                Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, Train("guidescope", 200, ("cam3", "Imaging")))).Ok.Value);
                Assert.True((await Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Mount1" })).Ok.Value);

                var scope = new ScopeDefinition { Id = "rig", DisplayName = "Rig", GuideShooterId = "main", DitherEvery = 3 };
                scope.Pointers.Add("eq");
                scope.Shooters.Add(new ScopeShooterRef { Id = "main" }); scope.Shooters.Add(new ScopeShooterRef { Id = "wide", OffsetEastArcmin = 12 });
                var r = await Commands.CallAsync(node, ScopeIds.Define, scope);
                Assert.False(r.Ok.Value); Assert.Contains("cannot image and guide", r.Error.Text);
                scope.GuideShooterId = TrainIds.GuideShooter("main");   // the OAG of the main train
                Assert.True((await Commands.CallAsync(node, ScopeIds.Define, scope)).Ok.Value);
                // the scope owns a guider: pulses go to its mount's guide port
                var guider = Assert.Single((await node.CallFunctionAsync<NOTESVoid, GuiderState>(GuiderIds.GetState("rig-guider"), NOTESVoid.Void))!);
                Assert.Equal("Pulse", guider.Output.Text);
                var train = Assert.Single((await node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState("main"), NOTESVoid.Void))!);
                Assert.Equal("main-guide", train.GuideShooterId.Text);
                Assert.Equal(2, train.Cameras.Count);
            }

            // everything comes back from the file
            using var node2 = ElinkNode.Create("TC2-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
            await using var again = new CompositionHost(node2, file);
            await again.StartAsync();
            var snap = again.Snapshot();
            Assert.Equal(new[] { "guidescope", "main", "wide" }, snap.Trains.Select(t => t.Id.Text).Order().ToArray());
            var main = snap.Trains.First(t => t.Id.Text == "main");
            Assert.Equal(800, main.FocalLengthMm.Value);
            Assert.Equal("Guiding", main.Cameras.First(c => c.CameraId.Text == "oag").Role.Text);
            var rig = snap.Scopes.Single();
            Assert.Equal("main-guide", rig.GuideShooterId.Text);
            Assert.Equal(3, rig.DitherEvery.Value);
            Assert.Equal(2, rig.Shooters.Count);
            Assert.Single((await node2.CallFunctionAsync<NOTESVoid, GuiderState>(GuiderIds.GetState("rig-guider"), NOTESVoid.Void))!);
        }
        finally { try { File.Delete(file); } catch { } }
    }
}

/// <summary>An imaging train with an off-axis guider, on INDI's simulators: the scope images with the train and
/// guides with the train's own guide camera, dithering between frames, all by itself.</summary>
public class TrainStackTests(ITestOutputHelper log) : IAsyncLifetime
{
    private IndiServerProcess _indi = null!;
    public async Task InitializeAsync()
    {
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_ccd", "indi_simulator_guide", "indi_simulator_wheel");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task AScopeImagesWithATrainAndGuidesWithItsOffAxisCamera()
    {
        using var node = ElinkNode.Create("TS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        await using var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port); await link.StartAsync();
        await using var compose = new CompositionHost(node); await compose.StartAsync();
        await using var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "CCD Simulator", "Guide Simulator", "Filter Simulator" })
        { await c.WaitForAsync(d, "CONNECTION", _ => true, T); await c.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        await c.WaitForAsync("Guide Simulator", "SCOPE_INFO", _ => true, T);
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", _ => true, T);

        // one OTA: imaging camera behind a filter wheel, and an off-axis guider; 400 mm
        var train = new ImagingTrainDefinition { Id = "main", Label = "400 mm + OAG", FocalLengthMm = 400, ApertureMm = 80, FilterWheelId = "Filter_Simulator" };
        train.Cameras.Add(new TrainCamera { CameraId = "CCD_Simulator", Role = "Imaging" });
        train.Cameras.Add(new TrainCamera { CameraId = "Guide_Simulator", Role = "Guiding" });
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, train)).Ok.Value);
        using var train0 = new RemoteState<TrainState>(node, TrainIds.State("main"), TrainIds.GetState("main"));
        await train0.StartAsync();
        TrainState? ts = null;
        train0.Changed += s => ts = s;
        ts = train0.Latest;
        // the train hands its focal length to both cameras and works out their scales
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", p => p.Number("FOCAL_LENGTH") == 400, T);
        await c.WaitForAsync("Guide Simulator", "SCOPE_INFO", p => p.Number("FOCAL_LENGTH") == 400, T);
        Assert.True(await Eventually(() => ts is { Cameras.Count: 2 } && ts.Cameras.All(x => x.Connected.Value && !double.IsNaN(x.PixelScaleArcsec.Value)), 30000),
            ts is null ? "no train state" : string.Join("; ", ts.Cameras.Select(x => $"{x.CameraId.Text} {x.Role.Text} connected {x.Connected.Value} scale {x.PixelScaleArcsec.Value}")) + " | " + ts.Message.Text);
        Assert.Equal(2.68, ts!.Cameras.First(x => x.Role.Text == "Imaging").PixelScaleArcsec.Value, 2);
        Assert.Equal(1.24, ts.Cameras.First(x => x.Role.Text == "Guiding").PixelScaleArcsec.Value, 2);
        Assert.Equal(0.95, ts.Cameras.First(x => x.Role.Text == "Imaging").FieldWidthDegrees.Value, 2);

        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" })).Ok.Value);
        var scope = new ScopeDefinition
        {
            Id = "rig", DisplayName = "Rig", GuideShooterId = TrainIds.GuideShooter("main"), GuideExposureSeconds = 1,
            DitherEvery = 1, DitherPixels = 4, SettlePixels = 1.5, SettleSeconds = 2, SettleTimeoutSeconds = 120,
        };
        scope.Pointers.Add("eq"); scope.Shooters.Add(new ScopeShooterRef { Id = "main" });
        Assert.True((await Commands.CallAsync(node, ScopeIds.Define, scope)).Ok.Value);

        ScopeState? state = null; GuiderState? guider = null;
        var shots = new ConcurrentQueue<ShotEvent>();
        await node.HookEventAsync(ScopeIds.State("rig"), (ScopeState s) => state = s);
        await node.HookEventAsync(GuiderIds.State("rig-guider"), (GuiderState s) => guider = s);
        await node.HookEventAsync(ShooterIds.Shot("rig"), (ShotEvent s) => shots.Enqueue(s));
        var r = await Commands.CallAsync(node, ScopeIds.Command("rig", "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = 5.58, DecDegrees = -1.2, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 1, Filter = "Red" }, Count = 2,
        });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => state is { Observing.Value: false }, 400000), $"{state?.Phase.Text} {state?.Message.Text} | guider {guider?.Phase.Text} {guider?.Message.Text}");
        log.WriteLine($"scope: {state!.Phase.Text} {state.Message.Text}; guider: {guider?.Phase.Text}, {guider?.Calibration.Text}, {guider?.Dithers.Value} dithers, RMS {guider?.RmsTotalArcsec.Value:0.00}\"");
        Assert.Equal(2, state.ShotsDone.Value);
        Assert.Equal("", state.Message.Text);
        Assert.True(guider!.Calibrated.Value);
        Assert.Equal(1, guider.Dithers.Value);                     // between the two frames, not before the first
        Assert.Equal("Guiding", guider.Phase.Text);                // still guiding after the run
        // only the imaging camera's frames come out of the scope, through the wheel; the guide frames stay inside
        Assert.Equal(2, shots.Count);
        Assert.All(shots, s => { Assert.Equal("main-CCD_Simulator", s.Shooter.Text); Assert.Equal("Red", s.Filter.Text); });
        Assert.All(shots, s => Assert.Contains("FOCALLEN", System.Text.Encoding.ASCII.GetString(s.Data.Data, 0, 2880 * 2)));
    }
}
