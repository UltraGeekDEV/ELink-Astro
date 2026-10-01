using ELink.Core;
using ELink.Contracts.Equipment;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using ELink.IndiBridge.Adapters;
using Event.CoreFunctionality;

namespace ELink.IndiBridge;

/// <summary>The typed half of the translation: watches an INDI client, and for every device builds the adapters its
/// driver interface calls for (a CCD becomes a Camera, a telescope a Mount, ...), registering them in the directory.</summary>
public sealed class IndiEquipmentManager : IAsyncDisposable
{
    // INDI DRIVER_INTERFACE bit mask
    private const int Telescope = 1 << 0, Ccd = 1 << 1, Focuser = 1 << 3, Filter = 1 << 4, Dome = 1 << 5, Gps = 1 << 6, Weather = 1 << 7, Rotator = 1 << 12;

    private readonly TypeSafeEVentNode _node;
    private readonly DeviceDirectory _directory;
    private readonly string _server;
    private readonly Func<string, string> _idFor;
    private readonly Dictionary<string, List<IEquipmentAdapter>> _adapters = new();
    private readonly Dictionary<string, List<DeviceInfo>> _infos = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IndiClient? _client;
    private Func<IndiChange, Task>? _handler;

    /// <param name="idFor">maps an INDI device name to the id segment used in EVent IDs (default: the sanitised name)</param>
    public IndiEquipmentManager(TypeSafeEVentNode node, DeviceDirectory directory, string serverName, Func<string, string>? idFor = null)
    {
        _node = node; _directory = directory; _server = serverName;
        _idFor = idFor ?? EquipmentIds.Segment;
    }

    public async Task Attach(IndiClient client)
    {
        await Detach();
        _client = client;
        _handler = OnChange;
        client.Changed += _handler;
        foreach (var p in client.Properties.Where(p => p.Name == "DRIVER_INFO")) await EnsureAdaptersAsync(p);
    }

    public async Task Detach()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;
        client.Changed -= _handler;
        List<string> devices;
        await _lock.WaitAsync();
        try { devices = _adapters.Keys.ToList(); } finally { _lock.Release(); }
        foreach (var d in devices) await RemoveDeviceAsync(d);
    }

    private async Task OnChange(IndiChange change)
    {
        switch (change)
        {
            case PropertyDefined { Property.Name: "DRIVER_INFO" } d: await EnsureAdaptersAsync(d.Property); break;
            case PropertyUpdated { Property.Name: "DRIVER_INFO" } u: await EnsureAdaptersAsync(u.Property); break;
            case DeviceDeleted dd: await RemoveDeviceAsync(dd.Device); break;
        }
    }

    private async Task EnsureAdaptersAsync(IndiProperty driverInfo)
    {
        var client = _client;
        if (client is null) return;
        await _lock.WaitAsync();
        try
        {
            string device = driverInfo.Device;
            if (_adapters.ContainsKey(device)) return;
            int interfaces = (int)driverInfo.Number("DRIVER_INTERFACE");
            var ctx = new AdapterContext(_node, client, _server, device, _idFor(device));

            var made = new List<IEquipmentAdapter>();
            var infos = new List<DeviceInfo>();
            async Task Add(IEquipmentAdapter adapter)
            {
                made.Add(adapter);
                await adapter.StartAsync();
                infos.Add(adapter.Info);
            }
            if ((interfaces & Telescope) != 0) await Add(new MountAdapter(ctx));
            if ((interfaces & Ccd) != 0) await Add(new CameraAdapter(ctx));
            if ((interfaces & Focuser) != 0) await Add(new FocuserAdapter(ctx));
            if ((interfaces & Filter) != 0) await Add(new FilterWheelAdapter(ctx));
            if ((interfaces & Dome) != 0) await Add(new DomeAdapter(ctx));
            if ((interfaces & Gps) != 0) await Add(new GpsAdapter(ctx));
            if ((interfaces & Weather) != 0) await Add(new WeatherAdapter(ctx));
            if ((interfaces & Rotator) != 0) await Add(new RotatorAdapter(ctx));

            _adapters[device] = made;
            _infos[device] = infos;
            foreach (var info in infos) await _directory.AddAsync(info);
        }
        finally { _lock.Release(); }
    }

    private async Task RemoveDeviceAsync(string device)
    {
        List<IEquipmentAdapter>? adapters; List<DeviceInfo>? infos;
        await _lock.WaitAsync();
        try
        {
            if (!_adapters.Remove(device, out adapters)) return;
            _infos.Remove(device, out infos);
        }
        finally { _lock.Release(); }
        foreach (var a in adapters) await a.DisposeAsync();
        if (infos is not null) foreach (var i in infos) await _directory.RemoveAsync(i);
    }

    public async ValueTask DisposeAsync() => await Detach();
}
