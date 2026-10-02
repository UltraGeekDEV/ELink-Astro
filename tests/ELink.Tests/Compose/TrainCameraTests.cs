using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
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
}
