using System.Net;
using System.Net.Sockets;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

public class MosaicTests : IAsyncLifetime
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;
    private FakeShooter _shooter = null!;
    private SmartScope _scope = null!;
    private MosaicService _mosaic = null!;
    private MosaicState? _last;
    private readonly List<ShotEvent> _shots = new();

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("MO-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        _pointer = new FakePointer(_node, "mp", 10); await _pointer.StartAsync();
        _shooter = new FakeShooter(_node, "ms"); await _shooter.StartAsync();
        var def = new ScopeDefinition { Id = "ms1" }; def.Pointers.Add("mp"); def.Shooters.Add(new ScopeShooterRef { Id = "ms" });
        _scope = new SmartScope(_node, def); await _scope.StartAsync();
        _mosaic = new MosaicService(_node); await _mosaic.StartAsync();
        await _node.HookEventAsync(MosaicIds.State, (MosaicState s) => _last = s);
        await _node.HookEventAsync(ShooterIds.Shot("ms1"), (ShotEvent s) => { lock (_shots) _shots.Add(s); });
    }

    public async Task DisposeAsync()
    {
        await _mosaic.DisposeAsync(); await _scope.DisposeAsync(); await _shooter.DisposeAsync(); await _pointer.DisposeAsync();
        _node.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(15); }
        return cond();
    }

    private static MosaicFrame Frame(double w, double h, double rot = 0, double oe = 0, double on = 0) =>
        new() { WidthDegrees = w, HeightDegrees = h, RotationDegrees = rot, OffsetEastDegrees = oe, OffsetNorthDegrees = on };

    private static MosaicRequest Req(double fovW = 0.5, double fovH = 0.3, double target = 0.2, double seconds = 0.02, string label = "M31", params MosaicFrame[] frames)
    {
        var r = new MosaicRequest
        {
            Label = label, ScopeId = "ms1", TargetSeconds = target,
            Center = new SkyTarget { RaHours = 0.71, DecDegrees = 41.3, Epoch = "J2000" },
            FovWidthDegrees = fovW, FovHeightDegrees = fovH, Exposure = new ShooterExposure { Seconds = seconds },
        };
        foreach (var f in frames.Length > 0 ? frames : new[] { Frame(0.2, 0.2) }) r.Frames.Add(f);
        return r;
    }

    private Task<CommandResult> Cmd<T>(string id, T input) where T : IBinaryConvertible, new() => Commands.CallAsync(_node, id, input);
    private bool Finished => _last is { Phase.Text: "Done" or "Error" or "Aborted" };

    [Fact]
    public async Task PreviewSizesARequestAndRejectsNonsense()
    {
        var p = (await _node.CallFunctionAsync<MosaicRequest, MosaicPreview>(MosaicIds.Plan, Req(target: 600, seconds: 10)))!.Single();
        Assert.Equal("", p.Error.Text);
        Assert.True(p.StepoverDegrees.Value > 0 && p.MapCols.Value > 0 && p.EstimatedVisits.Value > 0 && p.EstimatedHours.Value > 0);

        var noFrames = Req(); noFrames.Frames.Clear();
        var badCentre = Req(); badCentre.Center.Epoch = "JNow";
        var pole = Req(); pole.Center.DecDegrees = 89.9;
        var noExposure = Req(seconds: 0);
        var rotationsWithoutRotator = Req(); rotationsWithoutRotator.FieldRotations.Add(90);
        foreach (var bad in new[] { noFrames, badCentre, pole, noExposure, rotationsWithoutRotator, Req(fovW: 0) })
            Assert.NotEqual("", (await _node.CallFunctionAsync<MosaicRequest, MosaicPreview>(MosaicIds.Plan, bad))!.Single().Error.Text);
    }

    [Fact]
    public async Task PaintsTheAreaWithSingleShotsAndWhatTheMountReceivedReallyCoversIt()
    {
        var req = Req(fovW: 0.5, fovH: 0.3, target: 0.3, seconds: 0.02, frames: new[] { Frame(0.2, 0.2) });
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => Finished), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.True(_last.MinSeconds.Value >= 0.3 - 1e-6, $"min {_last.MinSeconds.Value}");
        Assert.True(_last.MeanSeconds.Value <= 0.3 * 1.25, $"mean {_last.MeanSeconds.Value}");

        // single shots: one slew and one exposure per visit, and nothing else
        int visits = _last.Visits.Value;
        Assert.True(visits > 20);
        Assert.Equal(visits, _pointer.Gotos.Count); Assert.Equal(visits, _shooter.Exposures);
        Assert.All(_shooter.Requests, r => Assert.Equal(0.02, r.Seconds.Value, 6));

        // rebuild the coverage independently from the positions the mount was sent to: it must really reach the target
        var check = new CoverageMap(0.5, 0.3, 0.2 / 12);
        foreach (var g in _pointer.Gotos)
        {
            var (e, n) = Gnomonic.FromSky(0.71, 41.3, g.RaHours.Value, g.DecDegrees.Value);       // PositionAngle 0: east/north = x/y
            check.Paint(CoverageMap.Footprints(new Pose(e, n, 0), new[] { new FrameSpec(0.1, 0.1, 0, 0, 0) }, 0), 0.02);
        }
        Assert.True(check.Min() >= 0.3 - 1e-6, $"independent check: min {check.Min()}");

        // frames carry the label as their object; the published coverage map is the right size and its brightest byte is the fullest cell
        Assert.True(await Eventually(() => { lock (_shots) return _shots.Count == visits; }));
        lock (_shots) Assert.All(_shots, s => Assert.Equal("M31", s.ObjectName.Text));
        Assert.Equal(_last.MapCols.Value * _last.MapRows.Value, _last.Map.Data.Length);
        Assert.True(_last.Map.Data.Max() > 200);
    }

    [Fact]
    public async Task TheScopeKeepsMovingInSmallHops()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 0.6, fovH: 0.4, target: 0.6, seconds: 0.02))).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        var poses = _pointer.Gotos.Select(g => Gnomonic.FromSky(0.71, 41.3, g.RaHours.Value, g.DecDegrees.Value)).ToList();
        double step = _last!.PassHopDegrees.Value;
        Assert.True(step >= _last.StepoverDegrees.Value - 1e-9 && step < 0.2 / 3, $"the pass hop {step} is at least the stepover and a fraction of the frame");
        var hops = Enumerable.Range(1, poses.Count - 1).Select(i => Math.Sqrt(Math.Pow(poses[i].EastDegrees - poses[i - 1].EastDegrees, 2) + Math.Pow(poses[i].NorthDegrees - poses[i - 1].NorthDegrees, 2))).ToList();
        double median = hops.Order().ElementAt(hops.Count / 2);
        Assert.True(median <= step * 1.01, $"median hop {median} vs stepover {step}");
        Assert.True(hops.Count(h => h <= 0.2 * 1.5) >= hops.Count * 0.9, "almost every move is far smaller than a frame");
    }

    [Fact]
    public async Task TheProducerPlansAheadButOnlyAsFarAsTheLookahead()
    {
        _mosaic.Lookahead = 2;
        Assert.True((await Cmd(MosaicIds.Start, Req(target: 0.12, seconds: 0.06))).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal(2, _mosaic.MaxQueueDepth);
    }

    [Fact]
    public async Task HeterogeneousFramesAndARotatorPaintTheSameArea()
    {
        await using var rot = new FakeRotator(_node, "rot"); await rot.StartAsync();
        var req = Req(fovW: 0.5, fovH: 0.4, target: 0.24, seconds: 0.02, frames: new[] { Frame(0.2, 0.2), Frame(0.12, 0.08, rot: 30, oe: 0.05, on: -0.03) });
        req.RotatorId = "rot"; req.RotatorOffsetDegrees = 10; req.PositionAngleDegrees = 15;
        req.FieldRotations.Add(0); req.FieldRotations.Add(90);
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => Finished, 60000), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.True(_last.MinSeconds.Value >= 0.24 - 1e-6);
        Assert.Contains(rot.Moves, a => Math.Abs(a - 350) < 1e-6);       // field angle 0 with an offset of 10: rotator at -10 = 350
        Assert.Contains(rot.Moves, a => Math.Abs(a - 80) < 1e-6);        // field angle 90: rotator at 80
        Assert.True(rot.Moves.Count <= 8, $"the rotator moved {rot.Moves.Count} times");
    }

    [Fact]
    public async Task TheTargetAndStepoverCanBeChangedWhileItRuns()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(target: 0, seconds: 0.03))).Ok.Value);      // endless
        Assert.True(await Eventually(() => _last is { Visits.Value: >= 10 }));
        Assert.Equal("Running", _last!.Phase.Text);
        Assert.True((await Cmd(MosaicIds.SetStepover, (BinaryConvertibleDouble)0.05)).Ok.Value);
        Assert.True((await Cmd(MosaicIds.SetTarget, (BinaryConvertibleDouble)0.3)).Ok.Value);   // now it has an end
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last.Phase.Text);
        Assert.True(_last.MinSeconds.Value >= 0.3 - 1e-6);
        Assert.Equal(0.3, _last.TargetSeconds.Value, 6);

        Assert.False((await Cmd(MosaicIds.SetTarget, (BinaryConvertibleDouble)1)).Ok.Value);    // nothing runs any more
        Assert.False((await Cmd(MosaicIds.SetStepover, (BinaryConvertibleDouble)0.1)).Ok.Value);
    }

    [Fact]
    public async Task MaxVisitsStopsTheRun()
    {
        var req = Req(target: 0, seconds: 0.02); req.MaxVisits = 7;
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal(7, _last.Visits.Value);
    }

    [Fact]
    public async Task PauseHoldsAfterTheShotInProgressAndResumeCarriesOn()
    {
        var req = Req(target: 0.3, seconds: 0.1);
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Visits.Value: >= 2 }));
        Assert.True((await Cmd(MosaicIds.Pause, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Paused" }));
        int held = _last!.Visits.Value;
        await Task.Delay(600);
        Assert.Equal(held, _last.Visits.Value);
        Assert.True((await Cmd(MosaicIds.Resume, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last.Phase.Text);
    }

    [Fact]
    public async Task AbortStopsAnEndlessScanAndANewRunCanFollow()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(target: 0, seconds: 0.05))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Visits.Value: >= 3 }));
        Assert.True((await Cmd(MosaicIds.Abort, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Aborted", _last!.Phase.Text);
        var small = Req(fovW: 0.2, fovH: 0.2, target: 0.04, seconds: 0.02);
        Assert.True((await Cmd(MosaicIds.Start, small)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }));
    }

    [Fact]
    public async Task ValidatesAndRunsOneAtATime()
    {
        Assert.False((await Cmd(MosaicIds.Start, new MosaicRequest())).Ok.Value);
        Assert.False((await Cmd(MosaicIds.Start, Req(fovW: 0))).Ok.Value);
        Assert.False((await Cmd(MosaicIds.Pause, NOTESVoid.Void)).Ok.Value);
        Assert.True((await Cmd(MosaicIds.Start, Req(target: 0, seconds: 0.1))).Ok.Value);
        Assert.False((await Cmd(MosaicIds.Start, Req())).Ok.Value);
        await Cmd(MosaicIds.Abort, NOTESVoid.Void);
        Assert.True(await Eventually(() => Finished));
    }

    [Fact]
    public async Task UnsafeWeatherHoldsTheScan()
    {
        await using var wx = new FakeWeather(_node, "wmo"); await wx.StartAsync();
        var req = Req(target: 0.4, seconds: 0.1); req.WeatherId = "wmo";
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Visits.Value: >= 2 }));
        await wx.SetAsync("Unsafe");
        Assert.True(await Eventually(() => _last is { Phase.Text: "WaitingForWeather" }));
        int held = _last!.Visits.Value;
        await Task.Delay(700);
        Assert.InRange(_last.Visits.Value, held, held + 1);
        await wx.SetAsync("Safe");
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last.Phase.Text);
    }

    [Fact]
    public async Task AFailingScopeEndsTheRunInsteadOfHangingIt()
    {
        _pointer.StuckPhase = "Parked";
        Assert.True((await Cmd(MosaicIds.Start, Req())).Ok.Value);
        Assert.True(await Eventually(() => Finished, 20000), "the pipeline must stop when a stage fails");
        Assert.Equal("Error", _last!.Phase.Text);
        Assert.Contains("Parked", _last.Message.Text);
        Assert.Equal(0, _last.Visits.Value);
    }
}
