using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Compose;

/// <summary>What a train does for its cameras: cooling ramps (and the scope waiting for them), gain/offset presets,
/// per-filter focus offsets.</summary>
public class TrainCameraTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FakeCamera _cam = null!;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("TC-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _cam = new FakeCamera(_node, "c1"); await _cam.StartAsync();
    }

    public async Task DisposeAsync() { await _cam.DisposeAsync(); _node.Dispose(); }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private async Task<TrainCameraInfo> Info(string train = "t") =>
        Assert.Single((await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(train), NOTESVoid.Void))!).Cameras[0];

    private static ImagingTrainDefinition Train(Action<TrainCamera>? camera = null, Action<ImagingTrainDefinition>? train = null)
    {
        var def = new ImagingTrainDefinition { Id = "t", FocalLengthMm = 400 };
        var c = new TrainCamera { CameraId = "c1", Role = "Imaging" };
        camera?.Invoke(c);
        def.Cameras.Add(c);
        train?.Invoke(def);
        return def;
    }

    [Fact]
    public async Task CoolsSlowlyToTheSetPointThenWarmsUpAndSwitchesOff()
    {
        // 30 °C a minute, a step every 100 ms: 0.05 °C per step, 0 → -2 °C takes about four seconds
        await using var train = new ImagingTrain(_node, Train(c => { c.CoolTo = -2; c.CoolDegreesPerMinute = 30; c.WarmTo = 0; })) { CoolerTick = TimeSpan.FromMilliseconds(100) };
        await train.StartAsync();
        Assert.True(await Eventually(async () => (await Info()).Cooler.Text == "Cooling"));
        Assert.True(_cam.CoolerOn);
        Assert.True(await Eventually(async () => (await Info()).Cooler.Text == "Cold", 30000), $"{_cam.Temperature}");
        Assert.Equal(-2, _cam.Temperature, 6);
        List<double> down; lock (_cam.SetPoints) down = _cam.SetPoints.ToList();
        Assert.True(down.Count >= 30, $"{down.Count} steps");   // ramped, not set in one go
        for (int i = 1; i < down.Count; i++) Assert.True(down[i] <= down[i - 1] + 1e-9 && down[i - 1] - down[i] <= 0.05 + 1e-6, $"step {i}: {down[i - 1]} → {down[i]}");

        Assert.True((await Commands.CallAsync(_node, TrainIds.Warm("t"), NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(async () => (await Info()).Cooler.Text == "Warming"));
        Assert.True(await Eventually(async () => (await Info()).Cooler.Text == "Off", 30000), $"{_cam.Temperature}");
        Assert.False(_cam.CoolerOn);
        Assert.True(_cam.Temperature > -0.5);

        // and cool again on request
        Assert.True((await Commands.CallAsync(_node, TrainIds.Cool("t"), NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _cam.CoolerOn));
    }

    private static async Task<bool> Eventually(Func<Task<bool>> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (await cond()) return true; await Task.Delay(50); }
        return await cond();
    }

    [Fact]
    public async Task TheScopeWaitsForItsCamerasToBeCold()
    {
        await using var train = new ImagingTrain(_node, Train(c => { c.CoolTo = -2; c.CoolDegreesPerMinute = 30; })) { CoolerTick = TimeSpan.FromMilliseconds(100) };
        await train.StartAsync();
        await using var pointer = new FakePointer(_node, "pm", 20); await pointer.StartAsync();
        var def = new ScopeDefinition { Id = "s", DisplayName = "s", MeridianFlip = false };
        def.Pointers.Add("pm"); def.Shooters.Add(new ScopeShooterRef { Id = "t" });
        await using var scope = new SmartScope(_node, def); await scope.StartAsync();
        var phases = new List<string>();
        await _node.HookEventAsync(ScopeIds.State("s"), (ScopeState s) => { lock (phases) phases.Add(s.Phase.Text); });
        Assert.True(await Eventually(async () => (await Info()).Cooler.Text == "Cooling"));

        Assert.True((await Commands.CallAsync(_node, ShooterIds.Expose("s"), new ShooterExposure { Seconds = 0.05 })).Ok.Value);
        Assert.True(await Eventually(() => _cam.Exposures.Count == 1, 30000));
        Assert.Equal(-2, _cam.TemperatureAtExposure[0], 1);   // only once cold
        lock (phases) Assert.Contains("Cooling", phases);
    }

    [Fact]
    public async Task GainAndOffsetPresetsApplyUnlessTheExposureSaysOtherwise()
    {
        await using var train = new ImagingTrain(_node, Train(c => { c.Gain = 100; c.Offset = 50; }));
        await train.StartAsync();
        Assert.Equal("", (await Info()).Cooler.Text);   // no cooling asked
        Assert.True((await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01 })).Ok.Value);
        Assert.True(await Eventually(() => _cam.Exposures.Count == 1));
        await Task.Delay(100);
        Assert.True((await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01, Gain = 0, Offset = 10 })).Ok.Value);
        Assert.True(await Eventually(() => _cam.Exposures.Count == 2));
        Assert.Equal((100.0, 50.0), (_cam.Exposures[0].Gain.Value, _cam.Exposures[0].Offset.Value));
        Assert.Equal((0.0, 10.0), (_cam.Exposures[1].Gain.Value, _cam.Exposures[1].Offset.Value));
    }

    [Fact]
    public async Task ChangingFilterMovesTheFocuserByTheOffsetDifference()
    {
        await using var wheel = new FakeWheel(_node, "w", "L", "R", "Ha", "Dark"); await wheel.StartAsync();
        await using var focuser = new FakeFocuser(_node, "f", 1000); await focuser.StartAsync();
        await using var train = new ImagingTrain(_node, Train(train: d =>
        {
            d.FilterWheelId = "w"; d.FocuserId = "f";
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "L", Steps = 0 });
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "R", Steps = 30 });
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "Ha", Steps = 120 });
        }));
        await train.StartAsync();
        Assert.Equal(3, Assert.Single((await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState("t"), NOTESVoid.Void))!).FocusOffsets.Count);
        async Task Shoot(string filter)
        {
            int n = _cam.Exposures.Count;
            var r = await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01, Filter = filter });
            Assert.True(r.Ok.Value, r.Error.Text);
            Assert.True(await Eventually(() => _cam.Exposures.Count == n + 1));
            await Task.Delay(100);
        }
        await Shoot("R"); Assert.Equal(1030, focuser.Position);
        await Shoot("Ha"); Assert.Equal(1120, focuser.Position);
        await Shoot("Ha"); Assert.Equal(1120, focuser.Position);
        await Shoot("L"); Assert.Equal(1000, focuser.Position);
        await Shoot("Dark"); Assert.Equal(1000, focuser.Position);   // no offset known: left alone
        lock (focuser.Moves) Assert.Equal([1030, 1120, 1000], focuser.Moves);
    }

    [Fact]
    public async Task APseudoMonoCameraTakesTurnsToFocusItsChannelsAndTagsTheFrames()
    {
        await using var focuser = new FakeFocuser(_node, "f", 1000); await focuser.StartAsync();
        await using var train = new ImagingTrain(_node, Train(c => c.PseudoMono = true, d =>
        {
            d.FocuserId = "f";
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "R", Steps = -20 });
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "G", Steps = 0 });
            d.FocusOffsets.Add(new FilterFocusOffset { Filter = "B", Steps = 30 });
        }));
        await train.StartAsync();
        var tags = new List<string>();
        await _node.HookEventAsync(ShooterIds.Shot("t"), (ShotEvent s) => { lock (tags) tags.Add(s.PseudoChannel.Text); });
        async Task Shoot(string filter = "")
        {
            int n = _cam.Exposures.Count;
            var r = await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01, Filter = filter });
            Assert.True(r.Ok.Value, r.Error.Text);
            Assert.True(await Eventually(() => _cam.Exposures.Count == n + 1));
            await Task.Delay(100);
        }
        await Shoot(); Assert.Equal(980, focuser.Position);    // red is 20 steps in from green, the focus autofocus found
        await Shoot(); Assert.Equal(1000, focuser.Position);
        await Shoot(); Assert.Equal(1030, focuser.Position);
        await Shoot(); Assert.Equal(980, focuser.Position);    // and round again
        Assert.True(await Eventually(() => { lock (tags) return tags.Count == 4; }));
        lock (tags) Assert.Equal(["R", "G", "B", "R"], tags);

        // autofocus (or anyone) moved the focuser: that is green again, whatever was last shot
        Assert.True((await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, "f", "MoveTo"), (BinaryConvertibleInt32)2000)).Ok.Value);
        await Task.Delay(150);
        await Shoot("B"); Assert.Equal(2030, focuser.Position);
        var wrong = await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01, Filter = "Ha" });
        Assert.False(wrong.Ok.Value);
        Assert.Contains("R, G and B", wrong.Error.Text);
    }

    /// <summary>An RGGB frame of a few white stars; each colour is sharpest at its own focuser position.</summary>
    private static byte[] ColourStars(int position, params (string Channel, int Best)[] best)
    {
        const int W = 240, H = 240;
        var px = new ushort[W * H];
        var stars = new (double X, double Y)[] { (40, 40), (120, 50), (200, 60), (60, 120), (140, 130), (200, 140), (50, 200), (120, 210), (190, 200) };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c = (y % 2, x % 2) switch { (0, 0) => 0, (1, 1) => 2, _ => 1 };
                int b = best.First(t => t.Channel == "RGB"[c].ToString()).Best;
                double sigma = Math.Sqrt(2.2 * 2.2 + Math.Pow(0.05 * (position - b), 2));
                double v = 300;
                foreach (var (sx, sy) in stars) v += 6000 * 2.2 * 2.2 / (sigma * sigma) * Math.Exp(-((x - sx) * (x - sx) + (y - sy) * (y - sy)) / (2 * sigma * sigma));
                px[y * W + x] = (ushort)Math.Min(65535, v);
            }
        return ELink.Imaging.FitsImage.Write16(W, H, px, new Dictionary<string, string> { ["BAYERPAT"] = "'RGGB'" });
    }

    [Fact]
    public async Task APseudoMonoScopeFocusesEachColourAndKeepsTheOffsets()
    {
        await using var focuser = new FakeFocuser(_node, "f", 1000); await focuser.StartAsync();
        _cam.FrameMaker = () => ColourStars(focuser.Position, ("R", 1020), ("G", 1000), ("B", 985));
        await using var train = new ImagingTrain(_node, Train(c => c.PseudoMono = true, d => d.FocuserId = "f"));
        await train.StartAsync();
        IReadOnlyList<(string Filter, int Steps)>? kept = null;
        train.FocusOffsetsChanged += o => kept = o;
        await using var af = new ELink.Automation.AutofocusService(_node); await af.StartAsync();
        await using var pointer = new FakePointer(_node, "pm", 20); await pointer.StartAsync();
        var def = new ScopeDefinition { Id = "s", DisplayName = "s", MeridianFlip = false, FocusOnStart = true, FocusStepSize = 10, FocusSamples = 9, FocusExposureSeconds = 0.05 };
        def.Pointers.Add("pm"); def.Shooters.Add(new ScopeShooterRef { Id = "t" });
        await using var scope = new SmartScope(_node, def); await scope.StartAsync();

        Assert.True((await Commands.CallAsync(_node, ShooterIds.Expose("s"), new ShooterExposure { Seconds = 0.05 })).Ok.Value);
        Assert.True(await Eventually(() => kept is not null, 60000), "the offsets were never measured");
        var offsets = kept!.ToDictionary(o => o.Filter, o => o.Steps);
        string all = string.Join(", ", offsets.Select(o => o.Key + "=" + o.Value));
        Assert.True(offsets["R"] is >= 16 and <= 24, all);
        Assert.True(offsets["B"] is >= -19 and <= -11, all);
        Assert.Equal(0, offsets["G"]);
        // the train says so too, and the focuser is on green, or on red once the exposure that followed has focused its first colour
        var state = Assert.Single((await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState("t"), NOTESVoid.Void))!);
        Assert.Equal(3, state.FocusOffsets.Count);
        Assert.True(state.Cameras[0].PseudoMono.Value);
        Assert.True(await Eventually(() => Math.Abs(focuser.Position - 1000) <= 3 || Math.Abs(focuser.Position - 1000 - offsets["R"]) <= 3), $"{focuser.Position}");
    }

    [Fact]
    public async Task APseudoMonoCameraFocusedByHandTagsFramesWithTheColourSaidToBeInFocus()
    {
        await using var train = new ImagingTrain(_node, Train(c => c.PseudoMono = true));   // no focuser: focused by hand
        await train.StartAsync();
        var tags = new List<string>();
        await _node.HookEventAsync(ShooterIds.Shot("t"), (ShotEvent s) => { lock (tags) tags.Add(s.PseudoChannel.Text); });
        async Task Shoot(string filter = "")
        {
            int n = _cam.Exposures.Count;
            var r = await Commands.CallAsync(_node, ShooterIds.Expose("t"), new ShooterExposure { Seconds = 0.01, Filter = filter });
            Assert.True(r.Ok.Value, r.Error.Text);
            Assert.True(await Eventually(() => _cam.Exposures.Count == n + 1));
            await Task.Delay(80);
        }
        Assert.Equal("G", (await Info()).PseudoChannel.Text);
        await Shoot(); await Shoot();
        Assert.True((await Commands.CallAsync(_node, TrainIds.SetPseudoChannel("t"), (BinaryConvertibleString)"r")).Ok.Value);
        Assert.Equal("R", (await Info()).PseudoChannel.Text);
        await Shoot();
        await Shoot("B");                                  // asked for outright
        Assert.False((await Commands.CallAsync(_node, TrainIds.SetPseudoChannel("t"), (BinaryConvertibleString)"X")).Ok.Value);
        Assert.True(await Eventually(() => { lock (tags) return tags.Count == 4; }));
        lock (tags) Assert.Equal(["G", "G", "R", "B"], tags);
    }
}
