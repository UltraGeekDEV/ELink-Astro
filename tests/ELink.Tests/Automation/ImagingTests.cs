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
    private ImagingState? _last;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("IM-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _fakes = new CommandSet(_node);
        await _fakes.AddAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, _ => Task.FromResult(_snap), "fake composition");
        _svc = new ImagingService(_node); await _svc.StartAsync();
        await _node.HookEventAsync(ImagingIds.State, (ImagingState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync();
        foreach (var o in Enumerable.Reverse(_owned)) await o.DisposeAsync();
        _fakes.Dispose(); _node.Dispose();
    }

    /// <summary>A scope of one mount and one train whose imaging camera covers w x h degrees.</summary>
    private async Task AddScopeAsync(string id, double w, double h, int slewMs = 20, bool withTrain = true)
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
            state.Cameras.Add(new TrainCameraInfo { CameraId = id + "-cam", Role = "Imaging", Connected = true, PixelScaleArcsec = 2.0, FieldWidthDegrees = w, FieldHeightDegrees = h });
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
