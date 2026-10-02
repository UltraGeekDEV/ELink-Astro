using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed record DeviceItem(string Kind, string Id, string DisplayName, string Source)
{
    public string Label => $"{DisplayName}";
    public string Key => Kind + "/" + Id;
}

public sealed record ScopeItem(string Id, string DisplayName, int Pointers, int Shooters)
{
    public string Detail => $"{Pointers} pointer{(Pointers == 1 ? "" : "s")}, {Shooters} shooter{(Shooters == 1 ? "" : "s")}";
}

/// <summary>What is out there on the mesh: equipment announced by backends and scopes composed by composition hosts.
/// Kept current by events; a late joiner starts from the List and Snapshot functions.</summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    private readonly MeshSession _mesh;
    private bool _started;
    private readonly Action<DeviceAnnouncement> _onAnnounce;
    private readonly Action<CompositionSnapshot> _onChanged;

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<ScopeItem> Scopes { get; } = new();
    /// <summary>The composition as last seen (every host merged): input for the composer.</summary>
    public CompositionSnapshot Composition { get; private set; } = new();
    public event Action? CompositionChanged;

    public CatalogViewModel(MeshSession mesh)
    {
        _mesh = mesh;
        _onAnnounce = a => UiThread.Post(() => Apply(a));
        _onChanged = snap => UiThread.Post(() => { Merge(snap); });
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        await _mesh.Node.HookEventAsync(EquipmentIds.Announce, _onAnnounce, "device catalog");
        await _mesh.Node.HookEventAsync(ScopeIds.Changed, _onChanged, "composition catalog");
        await RefreshAsync();
    }

    /// <summary>Asks every provider what it has; replaces the lists.</summary>
    public async Task RefreshAsync()
    {
        var lists = await _mesh.Node.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void);
        var items = (lists ?? new()).SelectMany(l => l.Devices)
            .Select(d => new DeviceItem(d.Kind.Text, d.Id.Text, d.DisplayName.Text, d.Source.Text)).ToList();
        var snaps = await _mesh.Node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void);
        UiThread.Post(() =>
        {
            Devices.Clear();
            foreach (var i in items.OrderBy(i => i.Kind).ThenBy(i => i.DisplayName)) Devices.Add(i);
            var merged = new CompositionSnapshot();
            foreach (var s in snaps ?? new())
            {
                foreach (var x in s.MountPointers) merged.MountPointers.Add(x);
                foreach (var x in s.CameraShooters) merged.CameraShooters.Add(x);
                foreach (var x in s.Scopes) merged.Scopes.Add(x);
                foreach (var x in s.Guiders) merged.Guiders.Add(x);
            }
            Merge(merged);
        });
    }

    private void Apply(DeviceAnnouncement a)
    {
        var item = new DeviceItem(a.Device.Kind.Text, a.Device.Id.Text, a.Device.DisplayName.Text, a.Device.Source.Text);
        var existing = Devices.FirstOrDefault(d => d.Key == item.Key);
        if (a.Present.Value)
        {
            if (existing is null)
            {
                int at = 0;
                while (at < Devices.Count && (string.CompareOrdinal(Devices[at].Kind, item.Kind) < 0 ||
                       (Devices[at].Kind == item.Kind && string.Compare(Devices[at].DisplayName, item.DisplayName, StringComparison.Ordinal) < 0))) at++;
                Devices.Insert(at, item);
            }
        }
        else if (existing is not null) Devices.Remove(existing);
    }

    private void Merge(CompositionSnapshot snap)
    {
        Composition = snap;
        Scopes.Clear();
        foreach (var s in snap.Scopes)
            Scopes.Add(new ScopeItem(s.Id.Text, s.DisplayName.Text != "" ? s.DisplayName.Text : s.Id.Text, s.Pointers.Count, s.Shooters.Count));
        CompositionChanged?.Invoke();
    }

    public IEnumerable<DeviceItem> OfKind(string kind) => Devices.Where(d => d.Kind == kind);

    public void Dispose()
    {
        if (!_started) return;
        try { _mesh.Node.UnhookEvent(EquipmentIds.Announce, _onAnnounce); _mesh.Node.UnhookEvent(ScopeIds.Changed, _onChanged); } catch (ObjectDisposedException) { }
    }
}
