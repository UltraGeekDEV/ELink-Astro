using System.Collections.Concurrent;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Indi.Client;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Xunit;
using Xunit.Abstractions;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Tests.Compose;

/// <summary>The scope centres by itself after its slews: on INDI's simulators, whose mount stops a few arcminutes from
/// where it is sent, it solves and re-aims by the error until the frame is on the target.</summary>
public class ScopeCentringTests(ITestOutputHelper log) : IAsyncLifetime
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

    [Fact]
    public async Task CentresAfterItsSlews()
    {
        if (_indi is null || PlateSolver.Locate() is not { } solveField) return;
        using var node = ElinkNode.Create("SC-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        await using var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port); await link.StartAsync();
        await using var solver = new PlateSolveService(node, new PlateSolver(solveField)); await solver.StartAsync();
        await using var compose = new CompositionHost(node); await compose.StartAsync();
        await using var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        var T = TimeSpan.FromSeconds(60);
        foreach (var d in new[] { "Telescope Simulator", "CCD Simulator" }) { await c.WaitForAsync(d, "CONNECTION", _ => true, T); await c.SetSwitchAsync(d, "CONNECTION", "CONNECT"); }
        await c.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", _ => true, T);
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", _ => true, T);

        var train = new ImagingTrainDefinition { Id = "main", FocalLengthMm = 400, ApertureMm = 80 };
        train.Cameras.Add(new TrainCamera { CameraId = "CCD_Simulator", Role = "Imaging" });
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, train)).Ok.Value);
        await c.WaitForAsync("CCD Simulator", "SCOPE_INFO", p => p.Number("FOCAL_LENGTH") == 400, T);
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" })).Ok.Value);
        var scope = new ScopeDefinition { Id = "rig", DisplayName = "rig", CenterAfterSlew = true, CenterToleranceArcmin = 0.5, CenterExposureSeconds = 2, MeridianFlip = false };
        scope.Pointers.Add("eq"); scope.Shooters.Add(new ScopeShooterRef { Id = "main" });
        Assert.True((await Commands.CallAsync(node, ScopeIds.Define, scope)).Ok.Value);

        ScopeState? state = null;
        var messages = new ConcurrentQueue<string>();
        var shots = new ConcurrentQueue<ShotEvent>();
        int solves = 0;
        await node.HookEventAsync(ScopeIds.State("rig"), (ScopeState s) => { state = s; if (s.Message.Text != "") messages.Enqueue(s.Message.Text); });
        await node.HookEventAsync(ShooterIds.Shot("rig"), (ShotEvent s) => shots.Enqueue(s));
        await node.HookEventAsync(SolveIds.Solved, (SolveResult r) => Interlocked.Increment(ref solves));

        async Task<double> ObserveAsync(double ra, double dec)
        {
            int before = shots.Count;
            var r = await Commands.CallAsync(node, ScopeIds.Command("rig", "Observe"), new ObserveRequest
            {
                Target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 2 }, Count = 1,
            });
            Assert.True(r.Ok.Value, r.Error.Text);
            Assert.True(await Eventually(() => state is { Observing.Value: false } && shots.Count > before, 400000), $"{state?.Phase.Text} {state?.Message.Text}");
            Assert.True(state!.Phase.Text != "Error", state.Message.Text);
            // where did the frame really land? solve it
            var frame = shots.Last();
            var check = Assert.Single((await node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
            {
                Image = frame.Data, HintRaHours = ra, HintDecDegrees = dec, HintRadiusDegrees = 3, ScaleLowArcsecPerPixel = 2.4, ScaleHighArcsecPerPixel = 3,
            }, TimeSpan.FromSeconds(120)))!);
            Assert.True(check.Solved.Value, check.Message.Text);
            return Sky.SeparationDegrees(check.RaHours.Value, check.DecDegrees.Value, ra, dec) * 60;
        }

        double first = await ObserveAsync(5.58, -1.2);
        log.WriteLine($"first target: frame centre {first:0.00}' from it; " + string.Join(" | ", messages.Distinct()));
        Assert.True(first < 0.7, $"centred to {first:0.00}'");
        Assert.Contains(messages, m => m.Contains("re-aiming"));   // the mount did not land within half an arcminute by itself
        Assert.Contains(messages, m => m.StartsWith("centred to"));

        // the next target: this simulator's goto error grows with the slew (it is not systematic), so it re-aims again
        messages.Clear();
        double second = await ObserveAsync(5.58 + 0.4 / 15, -0.9);
        log.WriteLine($"second target: {second:0.00}'; " + string.Join(" | ", messages.Distinct()));
        Assert.True(second < 0.7, $"centred to {second:0.00}'");
    }
}

/// <summary>A systematic pointing error (a mount that always lands 5' south-east, a guide scope's misalignment) is
/// learned once and aimed off on later slews nearby.</summary>
public class LearnedPointingTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;
    private FakeShooter _shooter = null!;
    private CommandSet _solver = null!;
    private int _solves;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("LP-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _pointer = new FakePointer(_node, "pm", 20); await _pointer.StartAsync();
        _shooter = new FakeShooter(_node, "cam"); await _shooter.StartAsync();
        _solver = new CommandSet(_node);
        // the frame lands 3' east and 4' south of wherever the mount was sent
        await _solver.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, r =>
        {
            Interlocked.Increment(ref _solves);
            var aim = _pointer.Gotos.Last();
            var (ra, dec) = Gnomonic.ToSky(aim.RaHours.Value, aim.DecDegrees.Value, 3.0 / 60, -4.0 / 60);
            return Task.FromResult(new SolveResult { Solved = true, RaHours = ra, DecDegrees = dec, ShooterId = r.ShooterId.Text });
        }, "systematic fake");
    }

    public async Task DisposeAsync() { _solver.Dispose(); await _shooter.DisposeAsync(); await _pointer.DisposeAsync(); _node.Dispose(); }

    [Fact]
    public async Task ASystematicErrorIsLearnedOnceAndAimedOffLater()
    {
        var def = new ScopeDefinition { Id = "s", DisplayName = "s", CenterAfterSlew = true, CenterToleranceArcmin = 0.5, CenterExposureSeconds = 0.05, MeridianFlip = false };
        def.Pointers.Add("pm"); def.Shooters.Add(new ScopeShooterRef { Id = "cam" });
        await using var scope = new SmartScope(_node, def); await scope.StartAsync();
        ScopeState? state = null;
        await _node.HookEventAsync(ScopeIds.State("s"), (ScopeState s) => state = s);

        async Task Observe(double ra, double dec)
        {
            Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("s", "Observe"), new ObserveRequest
            {
                Target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 0.05 }, Count = 1,
            })).Ok.Value);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until && state is not { Observing.Value: false, ShotsDone.Value: 1 }) await Task.Delay(20);
            Assert.True(state is { Observing.Value: false, ShotsDone.Value: 1 }, $"{state?.Phase.Text} {state?.Message.Text}");
        }

        await Observe(10, 20);
        Assert.Equal(2, _solves);                 // off by 5': re-aim, then centred
        Assert.Equal(2, _pointer.Gotos.Count);
        var aimed = _pointer.Gotos[1];
        var (e, n) = Gnomonic.FromSky(10, 20, aimed.RaHours.Value, aimed.DecDegrees.Value);
        Assert.Equal(-3.0, e * 60, 2); Assert.Equal(4.0, n * 60, 2);   // aimed 3' west and 4' north to compensate

        _solves = 0;
        await Observe(10.1, 21);                  // 1.8 degrees away: the learned correction applies at once
        Assert.Equal(1, _solves);
        Assert.Equal(3, _pointer.Gotos.Count);
        await Observe(16, -30);                   // far away: the correction is not trusted there
        Assert.Equal(1 + 2, _solves);
    }
}
