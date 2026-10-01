using System.Collections.Concurrent;
using ELink.Contracts.Equipment;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge;

/// <summary>The devices one node offers, announced on the mesh and listable by anyone: a consumer calls
/// <see cref="EquipmentIds.List"/> and, because a call reaches every provider, gets every bridge's devices.</summary>
public sealed class DeviceDirectory
{
    private readonly TypeSafeEVentNode _node;
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();

    public DeviceDirectory(TypeSafeEVentNode node) { _node = node; }

    public IReadOnlyCollection<DeviceInfo> Devices => _devices.Values.ToArray();

    public Task StartAsync() => _node.RegisterFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, _ =>
    {
        var list = new DeviceList();
        foreach (var d in _devices.Values) list.Devices.Add(d);
        return Task.FromResult(list);
    }, "the equipment this node provides (Mount, Camera, Focuser, ...)");

    public async Task AddAsync(DeviceInfo info)
    {
        _devices[Key(info)] = info;
        await Announce(true, info);
    }

    public async Task RemoveAsync(DeviceInfo info)
    {
        if (_devices.TryRemove(Key(info), out _)) await Announce(false, info);
    }

    private static string Key(DeviceInfo i) => i.Kind.Text + "/" + i.Id.Text;

    private async Task Announce(bool present, DeviceInfo info)
    {
        try { await _node.FireEventAsync(EquipmentIds.Announce, new DeviceAnnouncement { Present = present, Device = info }); }
        catch (ObjectDisposedException) { }
    }
}
