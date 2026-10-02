using System.Collections.Concurrent;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
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

/// <summary>The guider against INDI's telescope simulator and its guide camera simulator: calibration through real
/// pulses, guiding out a deliberate bump, a dither that settles, and the "you are here, should be here" output.</summary>
public class GuiderStackTests(ITestOutputHelper log) : IAsyncLifetime
{
    private IndiServerProcess _indi = null!;
    public async Task InitializeAsync()
    {
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope", "indi_simulator_guide");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private sealed record Rig(TypeSafeEVentNode Node, IndiClient Client, IAsyncDisposable[] Owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { foreach (var o in Owned.Reverse()) await o.DisposeAsync(); Node.Dispose(); }
    }

    private async Task<Rig> StartRigAsync(double guideFocalLength = 400)
    {
        var node = ElinkNode.Create("GD-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port); await link.StartAsync();
        var compose = new CompositionHost(node); await compose.StartAsync();
        var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "Guide Simulator" }) { await c.WaitForAsync(d, "CONNECTION", _ => true, T); await c.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        await c.SetSwitchAsync("Telescope Simulator", "ON_COORD_SET", "TRACK");
        var (jra, jdec) = Precession.J2000ToDate(5.58, -1.2, DateTime.UtcNow);
        await c.SetNumbersAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", new[] { ("RA", jra), ("DEC", jdec) });
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok && Math.Abs(p.Number("RA") - jra) < 0.01, TimeSpan.FromSeconds(180));
        await c.WaitForAsync("Guide Simulator", "SCOPE_INFO", _ => true, T);
        await c.SetNumbersAsync("Guide Simulator", "SCOPE_INFO", new[] { ("FOCAL_LENGTH", guideFocalLength), ("APERTURE", 80.0) });   // 400 mm: 1.24"/px
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineCameraShooter, new CameraShooterDefinition { Id = "gcam", CameraId = "Guide_Simulator" })).Ok.Value);
        Assert.True(await Eventually(() => Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = "North", Milliseconds = 0 }).GetAwaiter().GetResult().Ok.Value, 30000),
            "the telescope's guide port appears on the mesh");
        return new Rig(node, c, [link, compose, c]);
    }

    [Fact]
    public async Task CalibratesGuidesOutABumpAndDithers()
    {
        await using var rig = await StartRigAsync();
        var node = rig.Node;
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineGuider, new GuiderDefinition
        {
            Id = "g1", ShooterId = "gcam", Output = "Pulse", GuidePortId = "Telescope_Simulator", MountId = "Telescope_Simulator",
            ExposureSeconds = 1, CalibrationStepMs = 1000, CalibrationPixels = 20, SolveOrientation = false,
        })).Ok.Value);
        GuiderState? state = null;
        var steps = new ConcurrentQueue<GuideStep>();
        var corrections = new ConcurrentQueue<GuideCorrection>();
        await node.HookEventAsync(GuiderIds.State("g1"), (GuiderState s) => state = s);
        await node.HookEventAsync(GuiderIds.Step("g1"), (GuideStep s) => steps.Enqueue(s));
        await node.HookEventAsync(GuiderIds.Correction("g1"), (GuideCorrection s) => corrections.Enqueue(s));

        Assert.True((await Commands.CallAsync(node, GuiderIds.Start("g1"), new GuideStartRequest())).Ok.Value);
        Assert.True(await Eventually(() => state is { Phase.Text: "Guiding" or "Error" }, 300000), $"{state?.Phase.Text} {state?.Message.Text}");
        Assert.True(state!.Calibrated.Value, state.Message.Text);
        log.WriteLine($"calibration: {state.Calibration.Text}; scale {state.PixelScaleArcsec.Value:0.00}\"/px");
        Assert.Matches(@"axes at (8\d|9\d|10\d)°", state.Calibration.Text);
        Assert.Equal(1.24, state.PixelScaleArcsec.Value, 1);   // from the FITS header (FOCALLEN, PIXSIZE)

        // steady guiding: small errors
        int from = steps.Count;
        Assert.True(await Eventually(() => steps.Count >= from + 6, 90000));
        var steady = steps.Skip(from).ToList();
        log.WriteLine("steady: " + string.Join(", ", steady.Select(s => $"{Math.Sqrt(s.ErrorXPixels.Value * s.ErrorXPixels.Value + s.ErrorYPixels.Value * s.ErrorYPixels.Value):0.00}px")));
        Assert.All(steady.Skip(2), s => Assert.True(Math.Sqrt(s.ErrorXPixels.Value * s.ErrorXPixels.Value + s.ErrorYPixels.Value * s.ErrorYPixels.Value) < 2.5));

        // a bump: 3 s of East and North, about 22" each way (a gust, a cable snag): the guider brings the stars back
        await Task.WhenAll(
            Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = "East", Milliseconds = 3000 }),
            Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = "North", Milliseconds = 3000 }));
        from = steps.Count;
        Assert.True(await Eventually(() => steps.Skip(from).Any(s => Math.Abs(s.ErrorXPixels.Value) + Math.Abs(s.ErrorYPixels.Value) > 8), 30000), "the bump shows on the guide camera");
        var bumped = steps.Skip(from).First(s => Math.Abs(s.ErrorXPixels.Value) + Math.Abs(s.ErrorYPixels.Value) > 8);
        // against an East/North bump: West (+) and South (-) pulses
        Assert.True(bumped.RaPulseMs.Value > 0 && bumped.DecPulseMs.Value < 0, $"pulses {bumped.RaPulseMs.Value} {bumped.DecPulseMs.Value}");
        // the correction event says the same thing on the sky: the pointing went east and north, it must move west and south
        var bumpCorrection = corrections.First(c => c.Frame.Value == bumped.Frame.Value);
        Assert.True(bumpCorrection.MoveEastArcsec.Value < -8 && bumpCorrection.MoveNorthArcsec.Value < -8, $"move {bumpCorrection.MoveEastArcsec.Value:0.0}E {bumpCorrection.MoveNorthArcsec.Value:0.0}N");
        int bumpedAt = steps.Count;
        Assert.True(await Eventually(() => steps.Skip(bumpedAt).Count() >= 3 && steps.Skip(bumpedAt).TakeLast(2).All(s => Math.Sqrt(s.ErrorXPixels.Value * s.ErrorXPixels.Value + s.ErrorYPixels.Value * s.ErrorYPixels.Value) < 2.5), 90000),
            "back on the lock: " + string.Join(", ", steps.Skip(bumpedAt).Select(s => $"{s.ErrorXPixels.Value:0.0},{s.ErrorYPixels.Value:0.0}")));

        // dither: the lock moves, the guider follows and settles
        var d = await Commands.CallAsync(node, GuiderIds.Dither("g1"), new DitherRequest { Pixels = 6, SettlePixels = 1.5, SettleSeconds = 3, TimeoutSeconds = 120 }, TimeSpan.FromSeconds(150));
        Assert.True(d.Ok.Value, d.Error.Text);
        Assert.True(await Eventually(() => state is { Dithers.Value: 1, Settled.Value: true }, 10000));
        Assert.False(double.IsNaN(state!.RmsTotalArcsec.Value));
        log.WriteLine($"RMS {state.RmsRaArcsec.Value:0.00}\" RA, {state.RmsDecArcsec.Value:0.00}\" Dec");

        Assert.True((await Commands.CallAsync(node, GuiderIds.Stop("g1"), NOTESVoid.Void, TimeSpan.FromSeconds(30))).Ok.Value);
        Assert.True(await Eventually(() => state is { Phase.Text: "Idle" }, 10000));
    }

    /// <summary>"Goto guiding": a native device takes "you are here, should be here" and closes the loop itself. Here it
    /// is played by a few lines that turn the requested move into the telescope's own pulses at its guide rate.</summary>
    [Fact]
    public async Task CorrectionOutputLetsANativeDeviceCloseTheLoop()
    {
        if (PlateSolver.Locate() is not { } solveField) return;   // the guide camera's orientation comes from a solve
        await using var rig = await StartRigAsync(guideFocalLength: 200);   // a 53' field: plenty of index stars to solve
        var node = rig.Node;
        await using var solver = new PlateSolveService(node, new PlateSolver(solveField)); await solver.StartAsync();

        var received = new ConcurrentQueue<GuideCorrection>();
        using var target = new CommandSet(node);
        await target.AddAsync<GuideCorrection, CommandResult>(GuideTargetIds.Correct("native"), async c =>
        {
            received.Enqueue(c);
            const double rateArcsecPerMs = 0.5 * 15.041 / 1000;   // guide rate 0.5x sidereal
            double dec = Math.Cos(-1.2 * Math.PI / 180);
            int east = (int)Math.Round(c.MoveEastArcsec.Value * 0.8 / (rateArcsecPerMs * dec)), north = (int)Math.Round(c.MoveNorthArcsec.Value * 0.8 / rateArcsecPerMs);
            await Task.WhenAll(
                Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = east >= 0 ? "East" : "West", Milliseconds = Math.Min(5000, Math.Abs(east)) }),
                Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = north >= 0 ? "North" : "South", Milliseconds = Math.Min(5000, Math.Abs(north)) }));
            return CommandResult.Success();
        }, "a native mount's guide input");

        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineGuider, new GuiderDefinition
        {
            Id = "g2", ShooterId = "gcam", Output = "Correction", TargetId = "native", MountId = "Telescope_Simulator", ExposureSeconds = 1, SolveOrientation = true,
        })).Ok.Value);
        GuiderState? state = null;
        await node.HookEventAsync(GuiderIds.State("g2"), (GuiderState s) => state = s);
        Assert.True((await Commands.CallAsync(node, GuiderIds.Start("g2"), new GuideStartRequest())).Ok.Value);
        Assert.True(await Eventually(() => state is { Phase.Text: "Guiding" or "Error" }, 180000), $"{state?.Phase.Text} {state?.Message.Text}");
        Assert.True(state!.Phase.Text == "Guiding", state.Message.Text);
        Assert.False(state.Calibrated.Value);   // no pulse calibration needed: the device moves itself
        Assert.True(await Eventually(() => received.Count >= 2, 60000));

        // absolute positions: the solve put the lock on the sky, near where the mount says it points
        var first = received.First();
        Assert.False(double.IsNaN(first.IsRaHours.Value));
        Assert.True(Sky.SeparationDegrees(first.ShouldRaHours.Value, first.ShouldDecDegrees.Value, 5.58, -1.2) * 60 < 30,
            $"should be at {first.ShouldRaHours.Value} {first.ShouldDecDegrees.Value}");

        // bump it; the native device, told where it is and where it should be, brings it back
        await Task.WhenAll(
            Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = "West", Milliseconds = 3000 }),
            Commands.CallAsync(node, GuidePortIds.Pulse("Telescope_Simulator"), new GuidePulse { Direction = "South", Milliseconds = 3000 }));
        int n = received.Count;
        Assert.True(await Eventually(() => received.Skip(n).Any(c => c.MoveEastArcsec.Value > 10 && c.MoveNorthArcsec.Value > 10), 30000),
            "after a west/south bump it must move east and north: " + string.Join("; ", received.Skip(n).Select(c => $"{c.MoveEastArcsec.Value:0.0}E {c.MoveNorthArcsec.Value:0.0}N")));
        int m = received.Count;
        Assert.True(await Eventually(() => received.Skip(m).Count() >= 3 && received.Skip(m).TakeLast(2).All(c => Math.Abs(c.MoveEastArcsec.Value) < 3 && Math.Abs(c.MoveNorthArcsec.Value) < 3), 90000),
            "converged: " + string.Join("; ", received.Skip(m).Select(c => $"{c.MoveEastArcsec.Value:0.0}E {c.MoveNorthArcsec.Value:0.0}N")));
        await Commands.CallAsync(node, GuiderIds.Stop("g2"), NOTESVoid.Void, TimeSpan.FromSeconds(30));
    }
}
