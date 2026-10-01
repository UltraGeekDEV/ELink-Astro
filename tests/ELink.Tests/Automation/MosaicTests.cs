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
        _pointer = new FakePointer(_node, "mp", 30); await _pointer.StartAsync();
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

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(15); }
        return cond();
    }

    private static MosaicRequest Req(double fovW = 1.0, double fovH = 0.5, int passes = 1, double seconds = 0.05, string label = "M31") => new()
    {
        Label = label, ScopeId = "ms1", Passes = passes,
        Center = new SkyTarget { RaHours = 0.71, DecDegrees = 41.3, Epoch = "J2000" },
        FovWidthDegrees = fovW, FovHeightDegrees = fovH, FrameWidthDegrees = 0.5, FrameHeightDegrees = 0.5, Overlap = 0.2,
        Exposure = new ShooterExposure { Seconds = seconds },
    };

    private Task<CommandResult> Cmd<T>(string id, T input) where T : IBinaryConvertible, new() => Commands.CallAsync(_node, id, input);
    private bool Finished => _last is { Phase.Text: "Done" or "Error" or "Aborted" };

    [Fact]
    public async Task ScansEveryPanelOncePerPassWithSingleShotsAndNeverLongJumps()
    {
        var req = Req(fovW: 1.0, fovH: 0.9, passes: 3);          // 3 columns x 2 rows
        var layout = (await _node.CallFunctionAsync<MosaicRequest, MosaicLayout>(MosaicIds.Plan, req))!.Single();
        Assert.Equal(("", 3, 2), (layout.Error.Text, layout.Cols.Value, layout.Rows.Value));

        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => Finished), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal(18, _last.VisitsDone.Value);
        Assert.All(_last.Layout.Panels, p => Assert.Equal(3, p.Frames.Value));       // evenly covered: 3 single shots each

        // one slew and one single shot per visit, never a deep stack on one panel
        Assert.Equal(18, _pointer.Gotos.Count); Assert.Equal(18, _shooter.Exposures);
        Assert.All(_shooter.Requests, r => Assert.Equal(0.05, r.Seconds.Value, 6));
        // the scope keeps moving: every hop is to a neighbouring panel (or no hop at the end of a pass)
        double step = layout.StepXDegrees.Value;
        for (int i = 1; i < _pointer.Gotos.Count; i++)
        {
            double hop = Sky.SeparationDegrees(_pointer.Gotos[i - 1].RaHours.Value, _pointer.Gotos[i - 1].DecDegrees.Value, _pointer.Gotos[i].RaHours.Value, _pointer.Gotos[i].DecDegrees.Value);
            Assert.InRange(hop, 0, step * 1.05);
        }
        // frames know which panel they belong to
        Assert.True(await Eventually(() => { lock (_shots) return _shots.Count == 18; }));
        lock (_shots) { Assert.Equal(6, _shots.Select(s => s.ObjectName.Text).Distinct().Count()); Assert.All(_shots, s => { Assert.Matches(@"^M31_r[12]c[123]$", s.ObjectName.Text); Assert.Equal("M31", s.PlanId.Text); }); }
    }

    [Fact]
    public async Task TheProducerPlansAheadButOnlyAsFarAsTheLookahead()
    {
        _mosaic.Lookahead = 2;
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 1.5, fovH: 0.5, passes: 2, seconds: 0.15))).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal(2, _mosaic.MaxQueueDepth);         // it did run ahead (a pipeline), and never past the bound
    }

    [Fact]
    public async Task RoundsAndPanelsCanBeChangedWhileItRuns()
    {
        var req = Req(fovW: 1.0, fovH: 0.5, passes: 0, seconds: 0.1);          // 3 panels, until told otherwise
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => _last is { VisitsDone.Value: >= 4 }));
        Assert.Equal("Running", _last!.Phase.Text);

        Assert.True((await Cmd(MosaicIds.SkipPanel, new PanelSkip { Row = 0, Col = 1, Skip = true })).Ok.Value);
        Assert.True((await Cmd(MosaicIds.SetPasses, (BinaryConvertibleInt32)5)).Ok.Value);
        Assert.True(await Eventually(() => Finished), $"{_last?.Phase.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        var frames = _last.Layout.Panels.ToDictionary(p => p.Col.Value, p => p.Frames.Value);
        Assert.True(frames[1] < frames[0] && frames[1] < frames[2], $"the skipped middle panel must have fewer frames: {string.Join(",", frames.Values)}");
        Assert.True(_last.Layout.Panels.Single(p => p.Col.Value == 1).Skipped.Value);

        // refused when nothing runs, or when nothing would be left to scan
        Assert.False((await Cmd(MosaicIds.SetPasses, (BinaryConvertibleInt32)1)).Ok.Value);
        Assert.False((await Cmd(MosaicIds.SkipPanel, new PanelSkip())).Ok.Value);
    }

    [Fact]
    public async Task RefusesToSkipEveryPanelOrAnUnknownOne()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 0.5, fovH: 0.5, passes: 0, seconds: 0.2))).Ok.Value);   // a single panel
        Assert.False((await Cmd(MosaicIds.SkipPanel, new PanelSkip { Row = 0, Col = 0, Skip = true })).Ok.Value);
        Assert.False((await Cmd(MosaicIds.SkipPanel, new PanelSkip { Row = 5, Col = 5, Skip = true })).Ok.Value);
        await Cmd(MosaicIds.Abort, NOTESVoid.Void);
        Assert.True(await Eventually(() => Finished));
    }

    [Fact]
    public async Task PauseHoldsAfterTheShotInProgressAndResumeCarriesOn()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 1.0, passes: 3, seconds: 0.2))).Ok.Value);
        Assert.True(await Eventually(() => _last is { VisitsDone.Value: >= 1 }));
        Assert.True((await Cmd(MosaicIds.Pause, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Paused" }));
        int held = _last!.VisitsDone.Value; int gotos = _pointer.Gotos.Count;
        await Task.Delay(700);
        Assert.Equal(held, _last.VisitsDone.Value);
        Assert.True(_pointer.Gotos.Count <= gotos + 0, "nothing moves while paused");
        Assert.True((await Cmd(MosaicIds.Resume, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal(9, _last.VisitsDone.Value);
    }

    [Fact]
    public async Task AbortStopsAnEndlessScan()
    {
        Assert.True((await Cmd(MosaicIds.Start, Req(passes: 0, seconds: 0.1))).Ok.Value);
        Assert.True(await Eventually(() => _last is { VisitsDone.Value: >= 2 }));
        Assert.True((await Cmd(MosaicIds.Abort, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => Finished));
        Assert.Equal("Aborted", _last!.Phase.Text);
        // and a new run can start afterwards
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 0.5, fovH: 0.5, passes: 1))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }));
    }

    [Fact]
    public async Task ValidatesRequestsAndRunsOneAtATime()
    {
        Assert.False((await Cmd(MosaicIds.Start, new MosaicRequest())).Ok.Value);                          // no scope
        var bad = Req(); bad.FovWidthDegrees = 0;
        Assert.False((await Cmd(MosaicIds.Start, bad)).Ok.Value);
        Assert.False((await Cmd(MosaicIds.Pause, NOTESVoid.Void)).Ok.Value);                               // nothing runs
        Assert.True((await Cmd(MosaicIds.Start, Req(passes: 0, seconds: 0.2))).Ok.Value);
        Assert.False((await Cmd(MosaicIds.Start, Req())).Ok.Value);                                        // already running
        await Cmd(MosaicIds.Abort, NOTESVoid.Void);
        Assert.True(await Eventually(() => Finished));
    }

    [Fact]
    public async Task UnsafeWeatherHoldsTheScanAndSafeLetsItFinish()
    {
        await using var wx = new FakeWeather(_node, "wmo"); await wx.StartAsync();
        var req = Req(fovW: 1.0, passes: 4, seconds: 0.2); req.WeatherId = "wmo";
        Assert.True((await Cmd(MosaicIds.Start, req)).Ok.Value);
        Assert.True(await Eventually(() => _last is { VisitsDone.Value: >= 1 }));
        await wx.SetAsync("Unsafe");
        Assert.True(await Eventually(() => _last is { Phase.Text: "WaitingForWeather" }));
        int held = _last!.VisitsDone.Value;
        await Task.Delay(800);
        Assert.InRange(_last.VisitsDone.Value, held, held + 1);       // at most the shot already in progress
        await wx.SetAsync("Safe");
        Assert.True(await Eventually(() => Finished));
        Assert.Equal(12, _last.VisitsDone.Value);                      // 3 panels x 4 passes, none lost
    }

    [Fact]
    public async Task AFailingScopeEndsTheRunInsteadOfHangingIt()
    {
        _pointer.StuckPhase = "Parked";                                // the pointer never settles: every Observe fails
        Assert.True((await Cmd(MosaicIds.Start, Req(fovW: 1.5, passes: 2))).Ok.Value);
        Assert.True(await Eventually(() => Finished, 20000), "the pipeline must stop when a stage fails");
        Assert.Equal("Error", _last!.Phase.Text);
        Assert.Contains("Parked", _last.Message.Text);
        Assert.Equal(0, _last.VisitsDone.Value);
    }
}
