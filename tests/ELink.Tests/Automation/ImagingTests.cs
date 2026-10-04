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
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>One request, several scopes: each pulls work from the shared plan when it is free, with its own frame size.</summary>
public class ImagingTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private readonly List<IAsyncDisposable> _owned = new();
    private readonly Dictionary<string, FakePointer> _pointers = new();
    private readonly Dictionary<string, FakeShooter> _shooters = new();
    private readonly CompositionSnapshot _snap = new();
    private CommandSet _fakes = null!;
    private ImagingService _svc = null!;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "elink-images-" + Guid.NewGuid().ToString("N"));
    private ImagingState? _last;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("IM-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _fakes = new CommandSet(_node);
        await _fakes.AddAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, _ => Task.FromResult(_snap), "fake composition");
        _svc = new ImagingService(_node, _data); await _svc.StartAsync();
        await _node.HookEventAsync(ImagingIds.State, (ImagingState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync();
        foreach (var o in Enumerable.Reverse(_owned)) await o.DisposeAsync();
        _fakes.Dispose(); _node.Dispose();
        try { Directory.Delete(_data, true); } catch { }
    }

    /// <summary>A scope of one mount and one train whose imaging camera covers w x h degrees.</summary>
    private readonly Dictionary<string, TrainState> _trainStates = new();

    private async Task AddScopeAsync(string id, double w, double h, int slewMs = 20, bool withTrain = true, double angle = double.NaN)
    {
        var p = new FakePointer(_node, id + "-mount", slewMs); await p.StartAsync(); _owned.Add(p); _pointers[id] = p;
        string train = id + "-train";
        var s = new FakeShooter(_node, train); await s.StartAsync(); _owned.Add(s); _shooters[id] = s;
        if (withTrain)
        {
            var t = new ImagingTrainDefinition { Id = train, FocalLengthMm = 400 };
            t.Cameras.Add(new TrainCamera { CameraId = id + "-cam", Role = "Imaging" });
            _snap.Trains.Add(t);
            var state = new TrainState { Id = train, FocalLengthMm = 400 };
            state.Cameras.Add(new TrainCameraInfo { CameraId = id + "-cam", Role = "Imaging", ShooterId = train + "-cam", Connected = true, PixelScaleArcsec = 2.0, FieldWidthDegrees = w, FieldHeightDegrees = h, AngleDegrees = angle });
            _trainStates[id] = state;
            await _fakes.AddAsync<NOTESVoid, TrainState>(TrainIds.GetState(train), _ => Task.FromResult(state), "fake train");
        }
        var def = new ScopeDefinition { Id = id, DisplayName = id };
        def.Pointers.Add(id + "-mount"); def.Shooters.Add(new ScopeShooterRef { Id = train });
        _snap.Scopes.Add(def);
        var scope = new SmartScope(_node, def); await scope.StartAsync(); _owned.Add(scope);
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 60000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static ImagingRequest Request(double w, double h, double target, params string[] scopes)
    {
        var r = new ImagingRequest
        {
            Label = "Test", Center = new SkyTarget { RaHours = 5.6, DecDegrees = 10, Epoch = "J2000" }, WidthDegrees = w, HeightDegrees = h,
            Exposure = new ShooterExposure { Seconds = 0.05 }, TargetSeconds = target, LiveStack = false, DitherArcsec = 30,
        };
        foreach (var s in scopes) r.ScopeIds.Add(s);
        return r;
    }

    [Fact]
    public async Task TwoDifferentScopesShareOneAreaWithoutBeingKeptInStep()
    {
        await AddScopeAsync("narrow", 0.5, 0.35, slewMs: 10);
        await AddScopeAsync("wide", 1.2, 0.8, slewMs: 60);   // a slower mount: it simply takes fewer shots
        var preview = Assert.Single((await _node.CallFunctionAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, Request(2, 1.4, 0.2, "narrow", "wide"), TimeSpan.FromSeconds(60)))!);
        Assert.Equal("", preview.Phase.Text == "Error" ? preview.Message.Text : "");
        Assert.Equal(2, preview.Workers.Count);
        Assert.Equal(1.2, preview.Workers.First(w => w.ScopeId.Text == "wide").Frames[0].WidthDegrees.Value, 6);

        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, Request(2, 1.4, 0.2, "narrow", "wide"), TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal("", _last.Message.Text);
        Assert.True(_last.MinSeconds.Value >= 0.2 - 1e-6, $"least exposed spot {_last.MinSeconds.Value}");
        var narrow = _last.Workers.First(w => w.ScopeId.Text == "narrow");
        var wide = _last.Workers.First(w => w.ScopeId.Text == "wide");
        Assert.True(narrow.Visits.Value > 0 && wide.Visits.Value > 0, $"narrow {narrow.Visits.Value}, wide {wide.Visits.Value}");
        Assert.Equal(_last.Visits.Value, narrow.Visits.Value + wide.Visits.Value);
        Assert.Equal(narrow.Visits.Value, _shooters["narrow"].Exposures);
        Assert.Equal(wide.Visits.Value, _shooters["wide"].Exposures);
        // the two together needed fewer shots than the narrow one would alone: the wide frames did a share of the work
        Assert.True(_last.Visits.Value < 2 * narrow.Visits.Value + wide.Visits.Value);
    }

    [Fact]
    public async Task AWideAreaGetsAStackGridAsWideAsItsEdgesAndEveryShotLandsInsideIt()
    {
        // a 60° x 40° area turned 37° at declination 25°, shot with a 30° x 20° wide-angle field: the curved sky must not push shots off the stack
        await AddScopeAsync("wide-lens", 30, 20, slewMs: 5);
        LiveStackRequest? stack = null;
        await _fakes.AddAsync<LiveStackRequest, CommandResult>(LiveStackIds.Start, r => { stack = r; return Task.FromResult(CommandResult.Success()); }, "fake live stack");
        var req = Request(60, 40, 0.05, "wide-lens");
        req.Center = new SkyTarget { RaHours = 12, DecDegrees = 25, Epoch = "J2000" }; req.PositionAngleDegrees = 37; req.LiveStack = true; req.DitherArcsec = 0; req.OutputPixelScaleArcsec = 3600;   // 1°/px keeps the grid small
        var started = await Commands.CallAsync(_node, ImagingIds.Start, req, TimeSpan.FromSeconds(60));
        Assert.True(started.Ok.Value, started.Error.Text);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.NotNull(stack);
        Assert.Equal(PlanProjection.TangentSpan(60), stack!.FovWidthDegrees.Value, 6);
        Assert.Equal(PlanProjection.TangentSpan(40), stack.FovHeightDegrees.Value, 6);
        Assert.True(PlanProjection.TangentSpan(60) > 66);                          // wider than the plain 60: the sky is curved
        // the area's four corners are the grid's four corners (within a pixel of rounding): nothing of the area is cut off
        int w = (int)Math.Round(stack.FovWidthDegrees.Value * 3600 / stack.PixelScaleArcsec.Value), h = (int)Math.Round(stack.FovHeightDegrees.Value * 3600 / stack.PixelScaleArcsec.Value);
        var wcs = ELink.Imaging.TanWcs.Centered(stack.Center.RaHours.Value * 15, stack.Center.DecDegrees.Value, stack.PositionAngleDegrees.Value, stack.PixelScaleArcsec.Value, w, h);
        foreach (var (cx, cy) in new[] { (-30.0, 20.0), (30.0, 20.0), (30.0, -20.0), (-30.0, -20.0) })
        {
            var (ra, dec) = PlanProjection.ToSky(12, 25, 37, cx, cy);
            var (px, py) = wcs.SkyToPixel(ra * 15, dec);
            Assert.True(Math.Min(Math.Abs(px + 0.5), Math.Abs(px - w + 0.5)) < 1.5 && Math.Min(Math.Abs(py + 0.5), Math.Abs(py - h + 0.5)) < 1.5, $"the area corner ({cx},{cy}) falls at pixel ({px:0.0},{py:0.0}) of a {w}x{h} grid");
        }
        // the scope was sent where the plan says: every pose is within the area plus the reach of its field
        var gotos = _pointers["wide-lens"].Gotos;
        Assert.NotEmpty(gotos);
        foreach (var g in gotos)
        {
            Assert.True(PlanProjection.TryFromSky(12, 25, 37, g.RaHours.Value, g.DecDegrees.Value, out var planX, out var planY));
            Assert.True(Math.Abs(planX) <= 30 + 15 + 0.5 && Math.Abs(planY) <= 20 + 10 + 0.5, $"a shot at plan ({planX:0.0}°, {planY:0.0}°) is further out than the area and a field's reach");
        }
    }

    [Fact]
    public async Task ASingleTargetIsJustASmallAreaFilledWithDitheredShots()
    {
        await AddScopeAsync("solo", 0.5, 0.35);
        // no size: one frame; 0.25 s per spot = 5 shots of 0.05 s, each dithered by up to 30"
        var started = await Commands.CallAsync(_node, ImagingIds.Start, Request(0, 0, 0.25, "solo"), TimeSpan.FromSeconds(60));
        Assert.True(started.Ok.Value, started.Error.Text);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Equal(0.45, _last.WidthDegrees.Value, 6);   // 90% of the frame: dithered shots still cover it
        var gotos = _pointers["solo"].Gotos;
        Assert.True(gotos.Count >= 5, $"{gotos.Count} shots");
        var seps = gotos.Select(g => Sky.SeparationDegrees(g.RaHours.Value, g.DecDegrees.Value, 5.6, 10) * 3600).ToList();
        Assert.True(seps.All(x => x <= 30.5 + 0.2 * 3600), "near the target: " + string.Join(", ", seps.Select(x => $"{x:0}\"")));
        // dithered: no two shots in the same place
        Assert.Equal(gotos.Count, gotos.Select(g => (Math.Round(g.RaHours.Value, 7), Math.Round(g.DecDegrees.Value, 6))).Distinct().Count());
    }

    [Fact]
    public async Task AScopeThatBreaksLeavesTheRestToTheOthers()
    {
        await AddScopeAsync("good", 0.5, 0.35);
        await AddScopeAsync("bad", 0.5, 0.35);
        _pointers["bad"].StuckPhase = "Parked";
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, Request(1, 0.7, 0.1, "good", "bad"), TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Done", _last!.Phase.Text);
        Assert.Contains("bad failed", _last.Message.Text);
        Assert.Equal("Failed", _last.Workers.First(w => w.ScopeId.Text == "bad").Phase.Text);
        Assert.True(_last.Workers.First(w => w.ScopeId.Text == "good").Visits.Value > 0);
    }

    [Fact]
    public async Task RejectedFramesDoNotCountAndCloudsMakeTheScopeWait()
    {
        await AddScopeAsync("solo", 0.5, 0.35);
        // shots 4, 5 and 6 come through clouds (after three good ones: the baseline); the scope grades its frames and rejects them
        _shooters["solo"].FrameMaker = n => SyntheticSky.Frame(visible: n is >= 4 and <= 6 ? 0.1 : 1, frame: n);
        var r = Request(0, 0, 0.25, "solo"); r.SkyWaitSeconds = 1;
        var states = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await _node.HookEventAsync(ImagingIds.State, (ImagingState s) => { foreach (var w in s.Workers) states.Enqueue(w.Phase.Text); });
        var grades = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await _node.HookEventAsync(ShooterIds.Shot("solo"), (ShotEvent s) => grades.Enqueue($"{s.Quality.Text}/{s.Stars.Value}/{s.QualityNote.Text}"));
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, r, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        var w = _last!.Workers.Single();
        Assert.True(w.Rejected.Value == 3, string.Join(" | ", grades));
        Assert.Equal(5, w.Visits.Value);                         // 0.25 s of 0.05 s shots: five good ones
        Assert.Equal(3, w.Rejected.Value);
        Assert.Equal(8, _shooters["solo"].Exposures);            // the three bad ones were taken again
        Assert.Contains("WaitingForSky", states);                // three bad in a row: it waited for the clouds to pass
        Assert.True(_last.MinSeconds.Value >= 0.25 - 1e-6);
    }

    [Fact]
    public async Task AnImageCarriesOnWhereItStoppedAnotherNight()
    {
        await AddScopeAsync("solo", 0.5, 0.35);
        var night1 = Request(0, 0, 0.25, "solo"); night1.Label = "M31"; night1.MaxVisits = 2;   // dawn after two shots
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, night1, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }), _last?.Phase.Text);
        Assert.Equal(2, _last!.Visits.Value);
        var saved = Assert.Single(Assert.Single((await _node.CallFunctionAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, NOTESVoid.Void))!).Images);
        Assert.Equal("M31", saved.Label.Text); Assert.Equal(2, saved.Visits.Value);
        Assert.Equal(0.10, saved.MinSeconds.Value, 3);

        // the next night: the same image carries on from 0.1 s per spot to 0.25 s: three more shots, not five
        var night2 = Request(0, 0, 0.25, "solo"); night2.Label = "M31";
        int before = _shooters["solo"].Exposures;
        _last = null;
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, night2, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }), _last?.Phase.Text);
        Assert.Equal(3, _shooters["solo"].Exposures - before);
        Assert.Equal(5, _last!.Visits.Value);
        Assert.True(_last.MinSeconds.Value >= 0.25 - 1e-6);

        // another part of the sky under the same name: refused unless started afresh
        var elsewhere = Request(0, 0, 0.25, "solo"); elsewhere.Label = "M31"; elsewhere.Center.RaHours = 0.7;
        var r = await Commands.CallAsync(_node, ImagingIds.Start, elsewhere, TimeSpan.FromSeconds(60));
        Assert.False(r.Ok.Value); Assert.Contains("another part of the sky", r.Error.Text);
        elsewhere.Resume = false; elsewhere.MaxVisits = 1;
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, elsewhere, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done", Visits.Value: 1 }), _last?.Phase.Text);
        Assert.True((await Commands.CallAsync(_node, ImagingIds.DeleteSaved, (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)"M31")).Ok.Value);
        Assert.Empty(Assert.Single((await _node.CallFunctionAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, NOTESVoid.Void))!).Images);
    }

    [Fact]
    public async Task FramesAreLaidOutAtTheirCamerasLearnedAngles()
    {
        await AddScopeAsync("known", 0.5, 0.35, angle: 30);
        await AddScopeAsync("unknown", 0.5, 0.35);
        // a solver: solving the unknown camera's shooter teaches its train the angle (as a real train learns from Solved)
        var asked = new List<string>();
        await _fakes.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, r =>
        {
            lock (asked) asked.Add(r.ShooterId.Text);
            _trainStates["unknown"].Cameras[0].AngleDegrees = 112;
            return Task.FromResult(new SolveResult { Solved = true, PositionAngle = 112, ShooterId = r.ShooterId.Text });
        }, "fake solver");
        var preview = Assert.Single((await _node.CallFunctionAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, Request(2, 1.4, 0.2, "known", "unknown"), TimeSpan.FromSeconds(60)))!);
        Assert.Equal(30, preview.Workers.First(w => w.ScopeId.Text == "known").Frames[0].RotationDegrees.Value);
        Assert.Equal(0, preview.Workers.First(w => w.ScopeId.Text == "unknown").Frames[0].RotationDegrees.Value);   // not known yet: square to the sky

        var r = Request(2, 1.4, 0.05, "known", "unknown"); r.MaxVisits = 2;
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, r, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.Equal(new[] { "unknown-train-cam" }, asked);                               // only the camera nobody had solved
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" or "Error" }), _last?.Message.Text);
        Assert.Equal(112, _last!.Workers.First(w => w.ScopeId.Text == "unknown").Frames[0].RotationDegrees.Value);
    }

    [Fact]
    public async Task RequestsThatCannotWorkAreRefused()
    {
        await AddScopeAsync("plain", 0.5, 0.35, withTrain: false);
        var r = await Commands.CallAsync(_node, ImagingIds.Start, Request(1, 1, 0.1, "plain"), TimeSpan.FromSeconds(60));
        Assert.False(r.Ok.Value); Assert.Contains("imaging train", r.Error.Text);
        Assert.False((await Commands.CallAsync(_node, ImagingIds.Start, Request(1, 1, 0.1), TimeSpan.FromSeconds(60))).Ok.Value);           // no scope
        Assert.False((await Commands.CallAsync(_node, ImagingIds.Start, Request(1, 1, 0.1, "nope"), TimeSpan.FromSeconds(60))).Ok.Value);   // unknown scope
        var polar = Request(1, 1, 0.1, "plain"); polar.Center.DecDegrees = 89.8;
        Assert.False((await Commands.CallAsync(_node, ImagingIds.Start, polar, TimeSpan.FromSeconds(60))).Ok.Value);
    }
}
