using System.Globalization;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>What a train sets for one of its cameras: gain/offset presets, and the focuser to move by the filters' focus offsets.</summary>
public sealed record CameraShooterOptions(double Gain = double.NaN, double Offset = double.NaN, string FocuserId = "", IReadOnlyDictionary<string, int>? FocusOffsets = null)
{
    /// <summary>A colour camera taking turns to bring its red, green and blue into focus (offsets named R, G, B); each frame is tagged with the channel in focus.</summary>
    public bool PseudoMono { get; init; }
}

/// <summary>Makes a Camera, optionally with its filter wheel, usable as a Shooter.</summary>
public sealed class CameraShooter : IAsyncDisposable
{
    private readonly CameraShooterOptions _options;
    /// <summary>A colour camera taking turns to focus red, green and blue.</summary>
    public bool IsPseudoMono => _options.PseudoMono;
    /// <summary>A pseudo mono camera whose focus is set by hand (the train has no focuser): it cannot take turns, it says which colour is in focus.</summary>
    public bool IsManualPseudoMono => _options.PseudoMono && _focuser is null;
    /// <summary>For a pseudo mono camera focused by hand: the colour that is in focus (R, G or B).</summary>
    public string ManualChannel { get; set; } = "G";
    private readonly RemoteState<FocuserState>? _focuser;
    private readonly TypeSafeEVentNode _node;
    private readonly string _id, _cameraId, _wheelId;
    private readonly RemoteState<CameraState> _camera;
    private readonly RemoteState<FilterWheelState>? _wheel;
    private readonly CommandSet _commands;
    private readonly StatePublisher<ShooterState> _publisher;
    private readonly Action<FrameEvent> _onFrame;
    private string _filterUsed = "";
    private static readonly string[] PseudoChannels = ["R", "G", "B"];
    private string _pseudoUsed = "", _pseudoAt = "G";   // autofocus settles on green
    private int _pseudoNext;
    private int? _pseudoLeftAt;                          // where the focuser was left after our last move: if it is elsewhere, autofocus has been at it

    public CameraShooter(TypeSafeEVentNode node, string shooterId, string cameraId, string? filterWheelId = null, CameraShooterOptions? options = null)
    {
        _node = node; _id = shooterId; _cameraId = cameraId; _wheelId = filterWheelId ?? "";
        _options = options ?? new();
        if (_options.FocuserId != "" && _options.FocusOffsets is not null && (_options.PseudoMono || _wheelId != "" && _options.FocusOffsets.Count > 0))
            _focuser = new(node, EquipmentIds.State(DeviceKinds.Focuser, _options.FocuserId), EquipmentIds.GetState(DeviceKinds.Focuser, _options.FocuserId));
        _camera = new(node, EquipmentIds.State(DeviceKinds.Camera, cameraId), EquipmentIds.GetState(DeviceKinds.Camera, cameraId));
        if (_wheelId != "")
            _wheel = new(node, EquipmentIds.State(DeviceKinds.FilterWheel, _wheelId), EquipmentIds.GetState(DeviceKinds.FilterWheel, _wheelId));
        _commands = new CommandSet(node);
        _publisher = new(node, ShooterIds.State(shooterId), ShooterIds.GetState(shooterId), Build);
        _onFrame = f => _ = RelayAsync(f);
    }

    public async Task StartAsync()
    {
        await _camera.StartAsync();
        _camera.Changed += __ => { _ = _publisher.PublishAsync(); };
        if (_wheel is not null) { await _wheel.StartAsync(); _wheel.Changed += __ => { _ = _publisher.PublishAsync(); }; }
        if (_focuser is not null) await _focuser.StartAsync();
        await _node.HookEventAsync(EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Frame"), _onFrame, "frame relay");
        await _commands.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), ExposeAsync, $"take an exposure with camera {_cameraId}");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ =>
            Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "AbortExposure"), NOTESVoid.Void), "abort the running exposure");
        await _publisher.StartAsync();
    }

    private async Task<CommandResult> ExposeAsync(ShooterExposure e)
    {
        string filter = e.Filter.Text;
        _pseudoUsed = "";
        if (_options.PseudoMono)
        {
            if (string.Equals(filter, "Raw", StringComparison.OrdinalIgnoreCase)) filter = "";   // as the camera has it, wherever the focuser is: an autofocus moves it itself and measures one colour
            else
            {
                // R, G or B asked for, or taking turns when nothing is: every frame has one channel in focus
                string? channel = PseudoChannels.FirstOrDefault(c => string.Equals(c, filter, StringComparison.OrdinalIgnoreCase));
                if (filter != "" && channel is null) return CommandResult.Fail($"{_id} is a pseudo mono camera: its filters are R, G and B, not {filter}");
                if (_focuser is null)
                {
                    // focused by hand: no moves and no taking turns, the frames are tagged with the colour the person focused
                    channel ??= ManualChannel;
                }
                else
                {
                    channel ??= PseudoChannels[_pseudoNext++ % 3];
                    var moved = await MovePseudoAsync(channel);
                    if (!moved.Ok.Value) return moved;
                }
                _pseudoUsed = channel;
                filter = "";
            }
        }
        else if (filter != "")
        {
            if (_wheel is null) return CommandResult.Fail($"shooter {_id} has no filter wheel, cannot select {filter}");
            var names = _wheel.Latest?.FilterNames.Select(n => n.Text).ToList() ?? new();
            int slot = names.FindIndex(n => string.Equals(n, filter, StringComparison.OrdinalIgnoreCase)) + 1;
            if (slot == 0) return CommandResult.Fail($"no filter named {filter}");
            if (_wheel.Latest?.Slot.Value != slot)
            {
                string before = CurrentFilter();
                var r = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.FilterWheel, _wheelId, "SelectSlot"), (BinaryConvertibleInt32)slot);
                if (!r.Ok.Value) return r;
                try { await _wheel.WaitAsync(s => s.Slot.Value == slot && !s.Moving.Value, TimeSpan.FromSeconds(90)); }
                catch (TimeoutException) { return CommandResult.Fail("filter wheel did not arrive in time"); }
                var refocus = await MoveFocusOffsetAsync(before, names[slot - 1]);
                if (!refocus.Ok.Value) return refocus;
            }
            filter = names[slot - 1];
        }
        else if (!_options.PseudoMono) filter = CurrentFilter();
        _filterUsed = filter;

        // the last frame can arrive a moment before the camera says it is idle again: wait for that rather than fail
        if (_camera.Latest is { Phase.Text: "Exposing" })
        {
            try { await _camera.WaitAsync(c => c.Phase.Text != "Exposing", TimeSpan.FromSeconds(Math.Max(10, e.Seconds.Value))); }
            catch (TimeoutException) { return CommandResult.Fail($"camera {_cameraId} is still busy with another exposure"); }
        }

        return await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Expose"), new ExposeRequest
        {
            Seconds = e.Seconds, FrameType = e.FrameType, BinX = e.BinX, BinY = e.BinY, Iso = e.Iso,
            Gain = double.IsNaN(e.Gain.Value) ? _options.Gain : e.Gain.Value, Offset = double.IsNaN(e.Offset.Value) ? _options.Offset : e.Offset.Value,
        });
    }

    /// <summary>The focuser follows the filters' focus offsets: moved by the difference between the old and the new filter's.</summary>
    private async Task<CommandResult> MoveFocusOffsetAsync(string from, string to)
    {
        if (_focuser is null || _options.FocusOffsets is not { } offsets) return CommandResult.Success();
        int? Offset(string f) => offsets.FirstOrDefault(kv => string.Equals(kv.Key, f, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : null;
        if (Offset(from) is not { } a || Offset(to) is not { } b || a == b) return CommandResult.Success();
        var f = _focuser.Latest;
        if (f is null || !f.Connected.Value) return CommandResult.Fail($"focuser {_options.FocuserId} is not connected (needed for the {to} focus offset)");
        int target = f.Position.Value + (b - a);
        string focuser = EquipmentIds.Command(DeviceKinds.Focuser, _options.FocuserId, f.CanMoveAbsolute.Value ? "MoveTo" : "MoveBy");
        var r = await Commands.CallAsync(_node, focuser, (BinaryConvertibleInt32)(f.CanMoveAbsolute.Value ? target : b - a));
        if (!r.Ok.Value) return CommandResult.Fail($"focus offset for {to}: {r.Error.Text}");
        try { await _focuser.WaitAsync(x => !x.Moving.Value && x.Position.Value == target, TimeSpan.FromSeconds(120)); }
        catch (TimeoutException) { return CommandResult.Fail($"focuser did not reach the {to} focus offset"); }
        return CommandResult.Success();
    }

    /// <summary>Brings a channel into focus: the focuser moves by the difference between its offset and that of the channel it was at.</summary>
    private async Task<CommandResult> MovePseudoAsync(string channel)
    {
        var f = _focuser!.Latest;
        if (f is null || !f.Connected.Value) return CommandResult.Fail($"focuser {_options.FocuserId} is not connected (needed to focus {channel})");
        if (_pseudoLeftAt is { } left && left != f.Position.Value) _pseudoAt = "G";   // moved by something else, autofocus most likely: it focused on green
        int Steps(string c) => _options.FocusOffsets is { } o && o.FirstOrDefault(kv => string.Equals(kv.Key, c, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : 0;
        int delta = Steps(channel) - Steps(_pseudoAt);
        if (delta != 0)
        {
            int target = f.Position.Value + delta;
            var r = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, _options.FocuserId, f.CanMoveAbsolute.Value ? "MoveTo" : "MoveBy"), (BinaryConvertibleInt32)(f.CanMoveAbsolute.Value ? target : delta));
            if (!r.Ok.Value) return CommandResult.Fail($"focus for {channel}: {r.Error.Text}");
            try { await _focuser.WaitAsync(x => !x.Moving.Value && x.Position.Value == target, TimeSpan.FromSeconds(120)); }
            catch (TimeoutException) { return CommandResult.Fail($"focuser did not reach the {channel} focus"); }
        }
        _pseudoAt = channel; _pseudoLeftAt = _focuser.Latest?.Position.Value;
        return CommandResult.Success();
    }

    private string CurrentFilter()
    {
        var w = _wheel?.Latest;
        return w is not null && w.Slot.Value >= 1 && w.Slot.Value <= w.FilterNames.Count ? w.FilterNames[w.Slot.Value - 1].Text : "";
    }

    private async Task RelayAsync(FrameEvent f)
    {
        try
        {
            await _node.FireEventAsync(ShooterIds.Shot(_id), new ShotEvent
            {
                Shooter = _id, Format = f.Format, ExposureSeconds = f.ExposureSeconds, FrameType = f.FrameType,
                Filter = _filterUsed, PseudoChannel = _pseudoUsed, Timestamp = f.Timestamp, Data = f.Data,
            });
        }
        catch (ObjectDisposedException) { }
    }

    private ShooterState Build()
    {
        var c = _camera.Latest;
        var s = new ShooterState { Filter = CurrentFilter() };
        if (c is null || !c.Connected.Value) { s.Phase = "Disconnected"; return s; }
        s.Phase = c.Phase.Text; s.ExposureRemaining = c.ExposureRemaining.Value; s.Message = c.Message.Text;
        if (_wheel?.Latest is { Moving.Value: true }) s.Message = "filter wheel moving";
        return s;
    }

    public ValueTask DisposeAsync()
    {
        _commands.Dispose(); _publisher.Dispose(); _camera.Dispose(); _wheel?.Dispose(); _focuser?.Dispose();
        try { _node.UnhookEvent(EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Frame"), _onFrame); } catch (ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }
}
