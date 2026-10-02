using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>An imaging train: optics, the cameras behind them (imaging, or guiding through an off-axis guider), filter
/// wheel, focuser, rotator. As a Shooter it exposes all its imaging cameras at once and relays their frames under its
/// own id; its guiding camera is the Shooter <c>&lt;id&gt;-guide</c>. It hands its focal length to its cameras (so
/// frames carry FOCALLEN) and publishes each camera's pixel scale and field of view.</summary>
public sealed class ImagingTrain : IAsyncDisposable
{
    private sealed record Member(TrainCamera Camera, string ShooterId, CameraShooter Shooter, RemoteState<CameraState> State, RemoteState<ShooterState> ShooterState, Action<ShotEvent> OnShot);

    private readonly TypeSafeEVentNode _node;
    private readonly ImagingTrainDefinition _def;
    private readonly string _id;
    private readonly List<Member> _imaging = new(), _guiding = new();
    private readonly CommandSet _commands;
    private readonly StatePublisher<ShooterState> _shooterPub;
    private readonly StatePublisher<TrainState> _trainPub;
    private readonly HashSet<string> _opticsGiven = new();
    private string _message = "";
    private readonly Dictionary<string, double> _angles = new();   // by camera id, learned from plate solves
    private Action<ELink.Contracts.Automation.SolveResult>? _onSolved;

    public ImagingTrain(TypeSafeEVentNode node, ImagingTrainDefinition definition)
    {
        _node = node; _def = definition; _id = definition.Id.Text;
        bool wheelUsed = false;
        int guides = 0;
        foreach (var c in definition.Cameras)
        {
            bool guiding = c.Role.Text == "Guiding";
            string sid = guiding ? (guides++ == 0 ? TrainIds.GuideShooter(_id) : TrainIds.GuideShooter(_id) + guides) : TrainIds.CameraShooter(_id, c.CameraId.Text);
            // the filter wheel sits in front of the first imaging camera
            string? wheel = !guiding && !wheelUsed && definition.FilterWheelId.Text != "" ? definition.FilterWheelId.Text : null;
            if (wheel is not null) wheelUsed = true;
            var member = new Member(c, sid, new CameraShooter(node, sid, c.CameraId.Text, wheel),
                new RemoteState<CameraState>(node, EquipmentIds.State(DeviceKinds.Camera, c.CameraId.Text), EquipmentIds.GetState(DeviceKinds.Camera, c.CameraId.Text)),
                new RemoteState<ShooterState>(node, ShooterIds.State(sid), ShooterIds.GetState(sid)),
                shot => _ = RelayAsync(shot));
            (guiding ? _guiding : _imaging).Add(member);
        }
        _commands = new CommandSet(node);
        _shooterPub = new(node, ShooterIds.State(_id), ShooterIds.GetState(_id), BuildShooter);
        _trainPub = new(node, TrainIds.State(_id), TrainIds.GetState(_id), BuildTrain);
    }

    public string Id => _id;
    private IEnumerable<Member> All => _imaging.Concat(_guiding);

    public async Task StartAsync()
    {
        foreach (var m in All)
        {
            await m.Shooter.StartAsync();
            await m.State.StartAsync();
            m.State.Changed += s => { _ = GiveOpticsAsync(m, s); _ = _trainPub.PublishAsync(); };
            await m.ShooterState.StartAsync();
            m.ShooterState.Changed += __ => { _ = _shooterPub.PublishAsync(); };
            if (m.State.Latest is { } now) _ = GiveOpticsAsync(m, now);
        }
        foreach (var m in _imaging) await _node.HookEventAsync(ShooterIds.Shot(m.ShooterId), m.OnShot, $"train {_id} frames");
        // every solve of one of its cameras tells where that camera's image up points on the sky
        _onSolved = r =>
        {
            if (!r.Solved.Value || double.IsNaN(r.PositionAngle.Value)) return;
            var m = All.FirstOrDefault(x => x.ShooterId == r.ShooterId.Text) ?? (r.ShooterId.Text == _id && _imaging.Count == 1 ? _imaging[0] : null);
            if (m is null) return;
            lock (_angles) _angles[m.Camera.CameraId.Text] = r.PositionAngle.Value;
            _ = _trainPub.PublishAsync();
        };
        await _node.HookEventAsync(ELink.Contracts.Automation.SolveIds.Solved, _onSolved, $"train {_id}: camera angles");
        await _commands.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), ExposeAsync, $"expose every imaging camera of train {_id}");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ => AbortAsync(), "abort the exposures");
        await _shooterPub.StartAsync();
        await _trainPub.StartAsync();
    }

    /// <summary>Once per connection: tell the camera which telescope it sits behind, and, for cameras whose driver
    /// cannot know it (DSLRs), what sensor it has.</summary>
    private async Task GiveOpticsAsync(Member m, CameraState s)
    {
        string key = m.Camera.CameraId.Text;
        if (!s.Connected.Value) { lock (_opticsGiven) _opticsGiven.Remove(key); return; }
        lock (_opticsGiven) if (!_opticsGiven.Add(key)) return;
        var c = m.Camera;
        if (c.PixelSizeUm.Value > 0 || c.SensorWidth.Value > 0)
        {
            var sensor = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, key, "SetSensor"),
                new SensorInfo { Width = c.SensorWidth.Value, Height = c.SensorHeight.Value, PixelSizeUm = c.PixelSizeUm.Value });
            if (!sensor.Ok.Value) { _message = $"{key}: {sensor.Error.Text}"; await _trainPub.PublishAsync(); }
        }
        if (!(_def.FocalLengthMm.Value > 0)) return;
        var r = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, key, TrainIds.CameraSetOptics),
            new CameraOptics { FocalLengthMm = _def.FocalLengthMm.Value, ApertureMm = _def.ApertureMm.Value });
        if (!r.Ok.Value)
        {
            lock (_opticsGiven) _opticsGiven.Remove(key);
            _message = $"{key}: {r.Error.Text}";
            await _trainPub.PublishAsync();
        }
    }

    private async Task<CommandResult> ExposeAsync(ShooterExposure e)
    {
        if (_imaging.Count == 0) return CommandResult.Fail($"train {_id} has no imaging camera");
        // the filter applies to the camera behind the wheel; the others take the same exposure unfiltered
        var results = await Task.WhenAll(_imaging.Select((m, i) => Commands.CallAsync(_node, ShooterIds.Expose(m.ShooterId), i == 0 ? e : new ShooterExposure
        {
            Seconds = e.Seconds.Value, FrameType = e.FrameType.Text, BinX = e.BinX.Value, BinY = e.BinY.Value, Gain = e.Gain.Value, Iso = e.Iso.Text,
        })));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task<CommandResult> AbortAsync()
    {
        var results = await Task.WhenAll(All.Select(m => Commands.CallAsync(_node, ShooterIds.Abort(m.ShooterId), NOTESVoid.Void)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task RelayAsync(ShotEvent shot)
    {
        try { await _node.FireEventAsync(ShooterIds.Shot(_id), shot); } catch (ObjectDisposedException) { }
    }

    private ShooterState BuildShooter()
    {
        var states = _imaging.Select(m => m.ShooterState.Latest).ToList();
        var s = new ShooterState { ShotsPerExposure = Math.Max(1, _imaging.Count) };
        if (states.Count == 0 || states.All(x => x is null || x.Phase.Text == "Disconnected")) { s.Phase = "Disconnected"; return s; }
        var live = states.Where(x => x is not null && x.Phase.Text != "Disconnected").Select(x => x!).ToList();
        s.Phase = live.Any(x => x.Phase.Text == "Error") ? "Error" : live.Any(x => x.Phase.Text == "Exposing") ? "Exposing" : "Idle";
        s.ExposureRemaining = live.Max(x => x.ExposureRemaining.Value);
        s.Filter = states[0]?.Filter.Text ?? "";
        s.Message = live.Select(x => x.Message.Text).FirstOrDefault(m => m != "") ?? "";
        return s;
    }

    private TrainState BuildTrain()
    {
        var t = new TrainState
        {
            Id = _id, Label = _def.Label.Text, FocalLengthMm = _def.FocalLengthMm.Value, ApertureMm = _def.ApertureMm.Value,
            FilterWheelId = _def.FilterWheelId.Text, FocuserId = _def.FocuserId.Text, RotatorId = _def.RotatorId.Text,
            GuideShooterId = _guiding.Count > 0 ? _guiding[0].ShooterId : "", Message = _message,
        };
        foreach (var m in All)
        {
            var c = m.State.Latest;
            var info = new TrainCameraInfo { CameraId = m.Camera.CameraId.Text, Role = m.Camera.Role.Text, ShooterId = m.ShooterId, Connected = c?.Connected.Value ?? false };
            lock (_angles) if (_angles.TryGetValue(m.Camera.CameraId.Text, out var angle)) info.AngleDegrees = angle;
            // the train's own sensor numbers win over a driver that does not know (a DSLR reporting zeros)
            double pixel = m.Camera.PixelSizeUm.Value > 0 ? m.Camera.PixelSizeUm.Value : c?.PixelSizeUm.Value ?? 0;
            int width = m.Camera.SensorWidth.Value > 0 ? m.Camera.SensorWidth.Value : c?.SensorWidth.Value ?? 0;
            int height = m.Camera.SensorHeight.Value > 0 ? m.Camera.SensorHeight.Value : c?.SensorHeight.Value ?? 0;
            int binX = Math.Max(1, c?.BinX.Value ?? 1), binY = Math.Max(1, c?.BinY.Value ?? 1);
            if (pixel > 0 && _def.FocalLengthMm.Value > 0)
            {
                double scale = 206.265 * pixel * binX / _def.FocalLengthMm.Value;
                info.PixelScaleArcsec = scale;
                if (width > 0 && height > 0)
                {
                    info.FieldWidthDegrees = scale * width / binX / 3600;
                    info.FieldHeightDegrees = scale * height / binY / 3600;
                }
            }
            t.Cameras.Add(info);
        }
        return t;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _imaging) { try { _node.UnhookEvent(ShooterIds.Shot(m.ShooterId), m.OnShot); } catch (ObjectDisposedException) { } }
        if (_onSolved is not null) { try { _node.UnhookEvent(ELink.Contracts.Automation.SolveIds.Solved, _onSolved); } catch (ObjectDisposedException) { } }
        _commands.Dispose(); _shooterPub.Dispose(); _trainPub.Dispose();
        foreach (var m in All) { await m.Shooter.DisposeAsync(); m.State.Dispose(); m.ShooterState.Dispose(); }
    }
}
