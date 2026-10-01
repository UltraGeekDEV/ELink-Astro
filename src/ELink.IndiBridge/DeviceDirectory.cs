using System.Collections.Concurrent;
using ELink.Contracts.Equipment;
using ELink.Contracts.Indi;
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

    private readonly ConcurrentDictionary<string, byte> _servers = new();

    public IReadOnlyCollection<DeviceInfo> Devices => _devices.Values.ToArray();

    /// <summary>Remember that this node serves an INDI server (listed by <see cref="IndiIds.Servers"/>).</summary>
    public void AddServer(string name) => _servers[name] = 0;

    public async Task StartAsync()
    {
        await _node.RegisterFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, _ =>
        {
            var list = new DeviceList();
            foreach (var d in _devices.Values) list.Devices.Add(d);
            return Task.FromResult(list);
        }, "the equipment this node provides (Mount, Camera, Focuser, ...)");
        await _node.RegisterFunctionAsync<NOTESVoid, IndiServerList>(IndiIds.Servers, _ =>
        {
            var list = new IndiServerList();
            foreach (var n in _servers.Keys.Order()) list.Names.Add(n);
            return Task.FromResult(list);
        }, "the INDI servers this node bridges");
    }

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
