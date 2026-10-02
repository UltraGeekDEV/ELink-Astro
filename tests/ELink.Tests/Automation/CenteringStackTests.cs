using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Indi;
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

namespace ELink.Tests.Automation;

/// <summary>The whole thing for real: astrometry.net, the INDI telescope, CCD (primary) and guide (guide scope) simulators. The guide
/// scope is knocked out of alignment with the primary and the mount is given a pointing error (both with simulator guide pulses,
/// which shift a camera's field). A slew is then commanded straight over INDI, as a hand controller or other software would; ELink
/// notices, centres with the guide scope, and the primary ends on the target. Needs solve-field; skipped (passes) without it.</summary>
public class CenteringStackTests(ITestOutputHelper output) : IAsyncLifetime
{
    private IndiServerProcess? _indi;

    public async Task InitializeAsync()
    {
        if (PlateSolver.Locate() is null) return;
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_ccd", "indi_simulator_guide");
        Assert.True(await _indi.WaitListeningAsync());
    }

    public Task DisposeAsync() { _indi?.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task AHandControllerSlewIsCentredOnThePrimaryThroughAMisalignedGuideScope()
    {
        if (_indi is null || PlateSolver.Locate() is not { } solveField) { output.WriteLine("solve-field not installed: skipped"); return; }
        int bridgePort = ELink.Testing.TestPorts.Next();
        using var bridge = ElinkNode.Create("CS-Bridge", bridgePort);
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", _indi.Port);
        await link.StartAsync();
        using var work = ElinkNode.Create("CS-Work", ELink.Testing.TestPorts.Next());
        await ElinkNode.JoinAsync(work, "127.0.0.1", bridgePort);
        await using var compose = new CompositionHost(work); await compose.StartAsync();
        await using var solve = new PlateSolveService(work, new PlateSolver(solveField)); await solve.StartAsync();
        await using var center = new CenteringService(work);
        await center.StartAsync();

        // a plain INDI client, standing in for the hand controller (and for the test's own knobs)
        await using var hand = new IndiClient("127.0.0.1", _indi.Port);
        await hand.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "CCD Simulator", "Guide Simulator" }) { await hand.WaitForAsync(d, "CONNECTION", _ => true, T); await hand.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await hand.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await hand.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK");
        foreach (var (d, fl) in new[] { ("CCD Simulator", 600.0), ("Guide Simulator", 200.0) })
        {
            await hand.WaitForAsync(d, "SCOPE_INFO", _ => true, T);
            await hand.SetNumbersAsync(d, "SCOPE_INFO", new[] { ("FOCAL_LENGTH", fl), ("APERTURE", fl / 6) });
        }
        Assert.True(await Eventually(() => dir.Devices.Count(x => x.Kind.Text == "Camera") == 2 && dir.Devices.Any(x => x.Kind.Text == "Mount")));
        Assert.True((await Commands.CallAsync(work, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "main", CameraId = "CCD_Simulator" })).Ok.Value);
        Assert.True((await Commands.CallAsync(work, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "guide", CameraId = "Guide_Simulator" })).Ok.Value);

        // start somewhere, then knock the guide scope 14' north of the primary (guide pulses shift only that camera's field)
        async Task GotoJ2000(double ra, double dec)
        {
            var (jra, jdec) = Precession.J2000ToDate(ra, dec, DateTime.UtcNow);
            await hand.SetSwitchAsync("Telescope Simulator", "ON_COORD_SET", "TRACK");
            await hand.SetNumbersAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", new[] { ("RA", jra), ("DEC", jdec) });
            await hand.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Busy, TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
            await hand.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok, TimeSpan.FromSeconds(180));
        }
        await GotoJ2000(5.55, -1.0);
        async Task Pulse(string device, string property, string element, int ms) => await hand.SetNumberAsync(device, property, element, ms);
        await Pulse("Guide Simulator", "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", 60000);
        await Pulse("Guide Simulator", "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", 60000);
        await Task.Delay(500);

        Assert.True((await Commands.CallAsync(work, CenteringIds.Configure, new CenteringConfig
        {
            Enabled = true, MountId = "Telescope_Simulator", GuideShooterId = "guide", PrimaryShooterId = "main", ExposureSeconds = 2,
            ToleranceArcmin = 1.0, MaxIterations = 6, UseOffset = true, SyncMount = true,
        })).Ok.Value);
        CenteringState? state = null;
        await work.HookEventAsync(CenteringIds.State, (CenteringState s) => state = s);
        var cal = await Commands.CallAsync(work, CenteringIds.CalibrateOffset, NOTESVoid.Void);
        Assert.True(cal.Ok.Value, cal.Error.Text);
        Assert.True(await Eventually(() => state is { OffsetKnown.Value: true }));
        output.WriteLine($"learned offset: {state!.OffsetEastArcmin.Value:0.0}' east, {state.OffsetNorthArcmin.Value:0.0}' north");
        Assert.InRange(state.OffsetNorthArcmin.Value, -18, -10);           // the primary is about 14' south of the guide scope

        // now give the mount a pointing error (both cameras' fields move), and slew somewhere else from the "hand controller"
        await Pulse("Guide Simulator", "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", 60000);
        await Pulse("CCD Simulator", "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", 60000);
        var target = (Ra: 5.62, Dec: -2.0);
        int before = state.Centerings.Value;
        await GotoJ2000(target.Ra, target.Dec);
        Assert.True(await Eventually(() => state is { Centerings.Value: var n } && n > before || state is { Phase.Text: "Failed" }, 300000), $"{state?.Phase.Text} {state?.Message.Text}");
        output.WriteLine($"centring: {state!.Phase.Text}, {state.Message.Text}");
        Assert.Equal("Centered", state.Phase.Text);

        // the proof: where does the primary camera actually look now?
        var check = Assert.Single((await work.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest { ShooterId = "main", ExposureSeconds = 2, HintRaHours = target.Ra, HintDecDegrees = target.Dec }, TimeSpan.FromSeconds(200)))!);
        Assert.True(check.Solved.Value, check.Message.Text);
        double off = Sky.SeparationDegrees(check.RaHours.Value, check.DecDegrees.Value, target.Ra, target.Dec) * 60;
        output.WriteLine($"primary centre {Sexagesimal.Format(check.RaHours.Value)} {Sexagesimal.Format(check.DecDegrees.Value, 0)}: {off:0.00}' from the target");
        Assert.True(off < 1.5, $"the primary is {off:0.00}' from the target");
    }
}

/// <summary>Own simulator server: a solve checks where the camera looks against where the mount says it is, which other tests'
/// leftovers in a shared simulator (stale snooped positions) would spoil.</summary>
public class PlateSolveTests : IAsyncLifetime
{
    private IndiServerProcess? _indi;
    public async Task InitializeAsync()
    {
        if (PlateSolver.Locate() is null) return;
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_ccd");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi?.Dispose(); return Task.CompletedTask; }

    [Fact]
    public void ParsesTheSolverReport()
    {
        var o = PlateSolver.Parse("""
            Field 1: solved with index index-tycho2-09.littleendian.fits.
            Field center: (RA,Dec) = (83.727117, -1.520057) deg.
            Field size: 57.2094 x 45.7867 arcminutes
            Field rotation angle: up is 3.5 degrees W of N
              RA,Dec = (83.7265,-1.52067), pixel scale 2.68231 arcsec/pix.
            """, 0.3);
        Assert.True(o.Solved);
        Assert.Equal(83.727117 / 15, o.RaHours, 6); Assert.Equal(-1.520057, o.DecDegrees, 6);
        Assert.Equal(57.2094 / 60, o.FieldWidthDegrees, 6);
        Assert.Equal(356.5, o.PositionAngle, 6);
        Assert.Equal(2.68231, o.PixelScale, 5);
        Assert.False(PlateSolver.Parse("Field 1 did not solve", 1).Solved);
    }

    [Fact]
    public async Task SolvesASimulatorFrameTakenWithAShooter()
    {
        if (_indi is null || PlateSolver.Locate() is not { } solveField) return;
        var server = _indi;
        int bridgePort = ELink.Testing.TestPorts.Next();
        using var bridge = ElinkNode.Create("PS-Bridge", bridgePort);
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", server.Port);
        await link.StartAsync();
        await using var compose = new CompositionHost(bridge); await compose.StartAsync();
        await using var solve = new PlateSolveService(bridge, new PlateSolver(solveField)); await solve.StartAsync();
        await using var c = new IndiClient("127.0.0.1", server.Port); await c.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "CCD Simulator" }) { await c.WaitForAsync(d, "CONNECTION", _ => true, T); await c.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        await c.SetSwitchAsync("Telescope Simulator", "ON_COORD_SET", "TRACK");
        var (jra, jdec) = Precession.J2000ToDate(5.58, -1.2, DateTime.UtcNow);
        await c.SetNumbersAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", new[] { ("RA", jra), ("DEC", jdec) });
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok && Math.Abs(p.Number("RA") - jra) < 0.01, TimeSpan.FromSeconds(180));
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", _ => true, T);
        await c.SetNumbersAsync("CCD Simulator", "SCOPE_INFO", new[] { ("FOCAL_LENGTH", 400.0), ("APERTURE", 80.0) });
        Assert.True((await Commands.CallAsync(bridge, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "solvecam", CameraId = "CCD_Simulator" })).Ok.Value);
        Assert.True((await Commands.CallAsync(bridge, EquipmentIds.Command("Camera", "CCD_Simulator", "Connect"), (BinaryConvertibleBool)true)).Ok.Value);

        var r = Assert.Single((await bridge.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve,
            new SolveRequest { ShooterId = "solvecam", ExposureSeconds = 2, HintRaHours = 5.58, HintDecDegrees = -1.2, HintRadiusDegrees = 5 }, TimeSpan.FromSeconds(200)))!);
        Assert.True(r.Solved.Value, r.Message.Text);
        var now = c.GetProperty("Telescope Simulator", "EQUATORIAL_EOD_COORD")!;
        var (mra, mdec) = Precession.DateToJ2000(now.Number("RA"), now.Number("DEC"), DateTime.UtcNow);
        Assert.True(Sky.SeparationDegrees(r.RaHours.Value, r.DecDegrees.Value, mra, mdec) * 60 < 1.0,
            $"solved at {r.RaHours.Value} {r.DecDegrees.Value}, the mount reports {mra} {mdec} (J2000), commanded {jra} -> {now.Number("RA")}");
        Assert.Equal(2.68, r.PixelScale.Value, 1);                          // 5.2 um pixels behind 400 mm
        Assert.True(r.HasWcs.Value);                                          // the full solution, for registration
        var wcs = LiveStackService.FromSolve(r, 1280, 1024);
        Assert.Equal(2.68, wcs.PixelScaleArcsec, 1);
        var (cra, cdec) = wcs.PixelToSky(639.5, 511.5);
        Assert.True(Sky.SeparationDegrees(cra / 15, cdec, r.RaHours.Value, r.DecDegrees.Value) * 60 < 0.2);
        // the angle read from a WCS (as with ASTAP) is the one solve-field reports
        double paDiff = Math.Abs(((AstapSolver.FromWcs(wcs, 1280, 1024, 0).PositionAngle - r.PositionAngle.Value) % 360 + 540) % 360 - 180);
        Assert.True(paDiff < 0.5, $"WCS angle {AstapSolver.FromWcs(wcs, 1280, 1024, 0).PositionAngle:0.00} vs reported {r.PositionAngle.Value:0.00}");
        Assert.Equal("astrometry.net", r.Solver.Text);
        // a request with a nonsense image says why instead of throwing
        var bad = Assert.Single((await bridge.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest { Image = new ELink.Contracts.RawBytes(new byte[] { 1, 2, 3 }) }, TimeSpan.FromSeconds(60)))!);
        Assert.False(bad.Solved.Value); Assert.NotEqual("", bad.Message.Text);
    }
}
