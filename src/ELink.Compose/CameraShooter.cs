using System.Globalization;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>Makes a Camera, optionally with its filter wheel, usable as a Shooter.</summary>
public sealed class CameraShooter : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id, _cameraId, _wheelId;
    private readonly RemoteState<CameraState> _camera;
    private readonly RemoteState<FilterWheelState>? _wheel;
    private readonly CommandSet _commands;
    private readonly StatePublisher<ShooterState> _publisher;
    private readonly Action<FrameEvent> _onFrame;
    private string _filterUsed = "";

    public CameraShooter(TypeSafeEVentNode node, string shooterId, string cameraId, string? filterWheelId = null)
    {
        _node = node; _id = shooterId; _cameraId = cameraId; _wheelId = filterWheelId ?? "";
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
        await _node.HookEventAsync(EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Frame"), _onFrame, "frame relay");
        await _commands.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), ExposeAsync, $"take an exposure with camera {_cameraId}");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ =>
            Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "AbortExposure"), NOTESVoid.Void), "abort the running exposure");
        await _publisher.StartAsync();
    }

    private async Task<CommandResult> ExposeAsync(ShooterExposure e)
    {
        string filter = e.Filter.Text;
        if (filter != "")
        {
            if (_wheel is null) return CommandResult.Fail($"shooter {_id} has no filter wheel, cannot select {filter}");
            var names = _wheel.Latest?.FilterNames.Select(n => n.Text).ToList() ?? new();
            int slot = names.FindIndex(n => string.Equals(n, filter, StringComparison.OrdinalIgnoreCase)) + 1;
            if (slot == 0) return CommandResult.Fail($"no filter named {filter}");
            if (_wheel.Latest?.Slot.Value != slot)
            {
                var r = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.FilterWheel, _wheelId, "SelectSlot"), (BinaryConvertibleInt32)slot);
                if (!r.Ok.Value) return r;
                try { await _wheel.WaitAsync(s => s.Slot.Value == slot && !s.Moving.Value, TimeSpan.FromSeconds(90)); }
                catch (TimeoutException) { return CommandResult.Fail("filter wheel did not arrive in time"); }
            }
            filter = names[slot - 1];
        }
        else filter = CurrentFilter();
        _filterUsed = filter;

        return await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Expose"), new ExposeRequest
        {
            Seconds = e.Seconds, FrameType = e.FrameType, BinX = e.BinX, BinY = e.BinY, Gain = e.Gain,
        });
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
                Filter = _filterUsed, Timestamp = f.Timestamp, Data = f.Data,
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
        _commands.Dispose(); _publisher.Dispose(); _camera.Dispose(); _wheel?.Dispose();
        try { _node.UnhookEvent(EquipmentIds.Command(DeviceKinds.Camera, _cameraId, "Frame"), _onFrame); } catch (ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }
}
