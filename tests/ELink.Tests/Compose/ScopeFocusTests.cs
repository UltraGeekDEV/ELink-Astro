using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Compose;

/// <summary>The scope refocuses its own trains when their triggers fire: at the start, after a while, when the
/// temperature moves, on a filter change. Focus frames never count as the scope's frames.</summary>
public class ScopeFocusTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;
    private FakeShooter _train = null!, _trainCam = null!;
    private FakeAutofocus _af = null!;
    private CommandSet _fakes = null!;
    private StatePublisher<FocuserState> _focuser = null!;
    private double _temperature = 10;
    private ScopeState? _state;
    private int _scopeShots;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("SF-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _pointer = new FakePointer(_node, "pm", 20); await _pointer.StartAsync();
        _train = new FakeShooter(_node, "t"); await _train.StartAsync();                 // the train as the scope sees it
        _trainCam = new FakeShooter(_node, "t-cam1"); await _trainCam.StartAsync();      // its camera, which autofocus uses
        _af = new FakeAutofocus(_node); await _af.StartAsync();
        _fakes = new CommandSet(_node);
        var ts = new TrainState { Id = "t", FocuserId = "foc", FocalLengthMm = 400 };
        ts.Cameras.Add(new TrainCameraInfo { CameraId = "cam1", Role = "Imaging", ShooterId = "t-cam1", Connected = true });
        await _fakes.AddAsync<NOTESVoid, TrainState>(TrainIds.GetState("t"), _ => Task.FromResult(ts), "fake train");
        _focuser = new StatePublisher<FocuserState>(_node, EquipmentIds.State(DeviceKinds.Focuser, "foc"), EquipmentIds.GetState(DeviceKinds.Focuser, "foc"),
            () => new FocuserState { Connected = true, Temperature = _temperature, CanMoveAbsolute = true });
        await _focuser.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _focuser.Dispose(); _fakes.Dispose();
        await _af.DisposeAsync(); await _trainCam.DisposeAsync(); await _train.DisposeAsync(); await _pointer.DisposeAsync();
        _node.Dispose();
    }

    private async Task<SmartScope> Scope(Action<ScopeDefinition> configure)
    {
        var def = new ScopeDefinition { Id = "s", DisplayName = "s", MeridianFlip = false };
        def.Pointers.Add("pm"); def.Shooters.Add(new ScopeShooterRef { Id = "t" });
        configure(def);
        var scope = new SmartScope(_node, def); await scope.StartAsync();
        await _node.HookEventAsync(ScopeIds.State("s"), (ScopeState s) => _state = s);
        await _node.HookEventAsync(ShooterIds.Shot("s"), (ShotEvent _) => Interlocked.Increment(ref _scopeShots));
        return scope;
    }

    private async Task Observe(int count, string filter = "")
    {
        _state = null;   // only this run's states count
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("s", "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = 5, DecDegrees = 10, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 0.05, Filter = filter }, Count = count,
        })).Ok.Value);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && _state is not { Observing.Value: false, Phase.Text: not "Pointing" }) await Task.Delay(20);
        Assert.True(_state is { Observing.Value: false }, $"{_state?.Phase.Text} {_state?.Message.Text}");
        Assert.Equal(count, _state!.ShotsDone.Value);
        Assert.True(_state.Phase.Text != "Error", _state.Message.Text);
    }

    [Fact]
    public async Task FocusesAtTheStartThenWhenTimeOrTemperatureSaySo()
    {
        await using var scope = await Scope(d => { d.FocusOnStart = true; d.RefocusEveryMinutes = 1.0 / 60; d.RefocusTemperatureDelta = 1; d.FocusExposureSeconds = 2; });
        await Observe(3);
        Assert.Equal(1, _af.Runs);                              // before the first round only
        var req = _af.Requests[0];
        Assert.Equal("t-cam1", req.ShooterId.Text);             // the train's camera, its own focuser
        Assert.Equal("foc", req.FocuserId.Text);
        Assert.Equal(2, req.ExposureSeconds.Value);
        Assert.Equal(3, _scopeShots);                           // the focus frames were not the scope's frames

        await Task.Delay(1200);                                 // a second passes: the time trigger fires
        await Observe(1);
        Assert.Equal(2, _af.Runs);

        _temperature = 8.5; await _focuser.PublishAsync();      // 1.5 °C colder
        await Observe(1);
        Assert.True(_af.Runs >= 3, $"{_af.Runs} runs");
    }

    [Fact]
    public async Task RefocusesOnAFilterChangeAndCarriesOnIfFocusFails()
    {
        await using var scope = await Scope(d => { d.RefocusOnFilterChange = true; });
        await Observe(2, "Red");
        Assert.Equal(0, _af.Runs);                              // no focus at start: the first round is the baseline
        await Observe(1, "Green");
        Assert.Equal(1, _af.Runs);
        Assert.Equal("Green", _af.Requests[0].Filter.Text);     // focused through the new filter
        await Observe(2, "Green");
        Assert.Equal(1, _af.Runs);                              // same filter: no refocus
        _af.Fail = true;
        await Observe(1, "Red");
        Assert.Equal(2, _af.Runs);
        Assert.Contains("focus error", _state!.Message.Text);   // noted, and the frame was still taken
        await Observe(1, "Red");
        Assert.Equal(2, _af.Runs);                              // a failed focus is not retried every round
    }
}
