using System.Diagnostics;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Imaging;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests.Automation;

/// <summary>Live stacking simulator frames registered by real plate solves, on its own simulator server.</summary>
public class LiveStackStackTests(ITestOutputHelper log) : IAsyncLifetime
{
    private IndiServerProcess? _indi;
    public async Task InitializeAsync()
    {
        if (PlateSolver.Locate() is null) return;
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_ccd");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi?.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Theory]
    [InlineData(4.0)]   // coarser than the 2.68"/px frames
    [InlineData(1.5)]   // finer
    public async Task StacksSolvedSimulatorFramesIntoTheField(double scale)
    {
        if (_indi is null || PlateSolver.Locate() is not { } solveField) return;
        using var bridge = ElinkNode.Create("LSS-Bridge", ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", _indi.Port);
        await link.StartAsync();
        await using var compose = new CompositionHost(bridge); await compose.StartAsync();
        await using var solve = new PlateSolveService(bridge, new PlateSolver(solveField)); await solve.StartAsync();
        await using var stack = new LiveStackService(bridge); await stack.StartAsync();
        LiveStackState? last = null;
        await bridge.HookEventAsync(LiveStackIds.State, (LiveStackState s) => last = s);

        await using var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "CCD Simulator" }) { await c.WaitForAsync(d, "CONNECTION", _ => true, T); await c.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        await c.SetSwitchAsync("Telescope Simulator", "ON_COORD_SET", "TRACK");
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", _ => true, T);
        await c.SetNumbersAsync("CCD Simulator", "SCOPE_INFO", new[] { ("FOCAL_LENGTH", 400.0), ("APERTURE", 80.0) });
        Assert.True((await Commands.CallAsync(bridge, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "cam", CameraId = "CCD_Simulator" })).Ok.Value);
        Assert.True((await Commands.CallAsync(bridge, EquipmentIds.Command("Camera", "CCD_Simulator", "Connect"), (BinaryConvertibleBool)true)).Ok.Value);

        async Task Slew(double raH, double dec)
        {
            var (jra, jdec) = Precession.J2000ToDate(raH, dec, DateTime.UtcNow);
            await c.SetNumbersAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", new[] { ("RA", jra), ("DEC", jdec) });
            await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok && Math.Abs(p.Number("RA") - jra) < 0.005 && Math.Abs(p.Number("DEC") - jdec) < 0.05, TimeSpan.FromSeconds(180));
            await Task.Delay(1500);   // let the simulator settle on the new position
        }

        var req = new LiveStackRequest
        {
            Label = "sim", Center = new SkyTarget { RaHours = 5.58, DecDegrees = -1.2, Epoch = "J2000" },
            FovWidthDegrees = 1.4, FovHeightDegrees = 1.0, PixelScaleArcsec = scale, Registration = "Solve", FramePixelScaleArcsec = 2.68,
        };
        req.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(bridge, LiveStackIds.Start, req)).Ok.Value);

        var sw = Stopwatch.StartNew();
        int expected = 0;
        foreach (var (ra, dec) in new[] { (5.58, -1.2), (5.58, -1.2), (5.58 + 0.25 / 15, -1.1) })
        {
            await Slew(ra, dec);
            Assert.True((await Commands.CallAsync(bridge, ShooterIds.Expose("cam"), new ShooterExposure { Seconds = 2, FrameType = "Light" })).Ok.Value);
            expected++;
            Assert.True(await Eventually(() => last is not null && last.FramesStacked.Value + last.FramesRejected.Value >= expected && last.FramesPending.Value == 0, 150000),
                $"{last?.FramesStacked.Value} stacked, {last?.FramesRejected.Value} rejected: {last?.Message.Text}");
            log.WriteLine($"{sw.Elapsed.TotalSeconds:0.0}s {last!.Message.Text}");
        }
        Assert.Equal(3, last!.FramesStacked.Value);
        Assert.Equal(0, last.FramesRejected.Value);
        Assert.Equal(scale, last.PixelScaleArcsec.Value, 6);
        Assert.InRange(last.CoveragePercent.Value, 40, 100);   // two 57'x46' footprints in a 84'x60' field

        var img = Assert.Single((await bridge.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest(), TimeSpan.FromSeconds(30)))!);
        Assert.True(img.Ok.Value, img.Message.Text);
        var fits = FitsImage.Parse(img.Image.Data);
        Assert.Equal((int)Math.Round(1.4 * 3600 / scale), fits.Width);
        var stars = StarField.Detect(fits, maxStars: 200);
        double hfr = stars.Stars.Select(s => s.Hfr).Order().ElementAt(stars.Stars.Count / 2);
        log.WriteLine($"{stars.Stars.Count} stars in the stack, median HFR {hfr:0.00} px = {hfr * scale:0.0}\"");
        Assert.True(stars.Stars.Count >= 20, $"{stars.Stars.Count} stars");
        // registered frames keep stars sharp: misregistered ones would double or smear them
        Assert.InRange(hfr * scale, 1.0, 12.0);

        // the stack is solvable on its own and lands where it was asked to be
        var again = Assert.Single((await bridge.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve,
            new SolveRequest { Image = img.Image, HintRaHours = 5.58, HintDecDegrees = -1.2, HintRadiusDegrees = 3, ScaleLowArcsecPerPixel = scale * 0.9, ScaleHighArcsecPerPixel = scale * 1.1 }, TimeSpan.FromSeconds(120)))!);
        Assert.True(again.Solved.Value, again.Message.Text);
        Assert.True(Sky.SeparationDegrees(again.RaHours.Value, again.DecDegrees.Value, 5.58, -1.2) * 60 < 0.5,
            $"stack solved at {again.RaHours.Value} {again.DecDegrees.Value}");
        Assert.Equal(scale, again.PixelScale.Value, 1);
    }
}
