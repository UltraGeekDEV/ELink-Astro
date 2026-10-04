using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>A piece of equipment on the mesh, with what it is doing now (kept current by <see cref="CatalogViewModel"/>).</summary>
public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(string kind, string id, string displayName, string source) { Kind = kind; Id = id; DisplayName = displayName; Source = source; }
    public string Kind { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Source { get; }
    public string Label => DisplayName;
    public string Key => Kind + "/" + Id;
    /// <summary>What kind of thing, in words ("Camera", "Filter wheel").</summary>
    public string KindText => Kind switch { DeviceKinds.FilterWheel => "Filter wheel", DeviceKinds.Gps => "GPS", _ => Kind };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Css))] private bool _connected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Css))] private bool _hasProblem;
    [ObservableProperty] private string _status = "not connected";
    /// <summary>Style class of the status dot: ok | error | (none).</summary>
    public string Css => HasProblem ? "error" : Connected ? "ok" : "";
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
    /// <summary>The devices a person works with: everything except plumbing such as guide ports.</summary>
    public ObservableCollection<DeviceItem> Equipment { get; } = new();
    private readonly Dictionary<string, (DeviceItem Item, IDisposable? Follower)> _tracked = new();
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
        var announced = (lists ?? new()).SelectMany(l => l.Devices)
            .Select(d => (Kind: d.Kind.Text, Id: d.Id.Text, Name: d.DisplayName.Text, Source: d.Source.Text)).ToList();
        var snaps = await _mesh.Node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void);
        UiThread.Post(() =>
        {
            var items = announced.Select(a => Track(new DeviceItem(a.Kind, a.Id, a.Name, a.Source))).ToList();
            foreach (var key in _tracked.Keys.Where(k => items.All(i => i.Key != k)).ToList()) Untrack(key);
            Devices.Clear();
            foreach (var i in items.OrderBy(i => i.Kind).ThenBy(i => i.DisplayName)) Devices.Add(i);
            SyncEquipment();
            var merged = new CompositionSnapshot();
            foreach (var s in snaps ?? new())
            {
                foreach (var x in s.MountPointers) merged.MountPointers.Add(x);
                foreach (var x in s.CameraShooters) merged.CameraShooters.Add(x);
                foreach (var x in s.Scopes) merged.Scopes.Add(x);
                foreach (var x in s.Guiders) merged.Guiders.Add(x);
                foreach (var x in s.Trains) merged.Trains.Add(x);
            }
            Merge(merged);
        });
    }

    /// <summary>The one item for a device (an earlier one is kept, so what is bound to it stays bound), and a follower of its state.</summary>
    private DeviceItem Track(DeviceItem fresh)
    {
        if (_tracked.TryGetValue(fresh.Key, out var have)) return have.Item;
        _tracked[fresh.Key] = (fresh, StartFollowing(fresh));
        return fresh;
    }

    private void Untrack(string key)
    {
        if (_tracked.Remove(key, out var t)) t.Follower?.Dispose();
    }

    private void SyncEquipment()
    {
        var wanted = Devices.Where(d => d.Kind != DeviceKinds.GuidePort).ToList();
        if (Equipment.SequenceEqual(wanted)) return;
        Equipment.Clear();
        foreach (var d in wanted) Equipment.Add(d);
    }

    /// <summary>Follows one device's state to keep its connected flag and status words current.</summary>
    private IDisposable? StartFollowing(DeviceItem item)
    {
        string state = EquipmentIds.State(item.Kind, item.Id), get = EquipmentIds.GetState(item.Kind, item.Id);
        void Set(bool connected, string text, bool problem = false)
        {
            item.Connected = connected; item.HasProblem = connected && problem;
            item.Status = connected ? text : "not connected";
        }
        static string Deg(double v) => double.IsNaN(v) ? "" : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        IFollower? f = item.Kind switch
        {
            DeviceKinds.Mount => new Follower<MountState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.Parked.Value ? "Parked" : s.Phase.Text, s.Phase.Text == "Error")),
            DeviceKinds.Camera => new Follower<CameraState>(_mesh.Node, state, get, s => Set(s.Connected.Value,
                (s.Phase.Text == "Exposing" ? $"Exposing ({s.ExposureRemaining.Value:0} s left)" : s.Phase.Text) + (double.IsNaN(s.Temperature.Value) ? "" : $" · {Deg(s.Temperature.Value)} °C"), s.Phase.Text == "Error")),
            DeviceKinds.Focuser => new Follower<FocuserState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.Moving.Value ? "Moving" : $"Position {s.Position.Value}")),
            DeviceKinds.FilterWheel => new Follower<FilterWheelState>(_mesh.Node, state, get, s => Set(s.Connected.Value,
                s.Moving.Value ? "Moving" : s.Slot.Value >= 1 && s.Slot.Value <= s.FilterNames.Count ? s.FilterNames[s.Slot.Value - 1].Text : $"Slot {s.Slot.Value}")),
            DeviceKinds.Rotator => new Follower<RotatorState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.Moving.Value ? "Moving" : $"{Deg(s.AngleDegrees.Value)}°")),
            DeviceKinds.Dome => new Follower<DomeState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.Moving.Value ? "Moving" : s.Shutter.Text)),
            DeviceKinds.Weather => new Follower<WeatherState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.Safety.Text, s.Safety.Text is "Unsafe" or "Dangerous")),
            DeviceKinds.Gps => new Follower<GpsState>(_mesh.Node, state, get, s => Set(s.Connected.Value, s.HasFix.Value ? "Has a fix" : "No fix yet")),
            _ => null,
        };
        if (f is not null) _ = StartQuietly(f);
        return f;
    }

    private static async Task StartQuietly(IFollower f) { try { await f.StartAsync(); } catch (Exception) { } }

    private void Apply(DeviceAnnouncement a)
    {
        var item = new DeviceItem(a.Device.Kind.Text, a.Device.Id.Text, a.Device.DisplayName.Text, a.Device.Source.Text);
        var existing = Devices.FirstOrDefault(d => d.Key == item.Key);
        if (a.Present.Value)
        {
            if (existing is null)
            {
                item = Track(item);
                int at = 0;
                while (at < Devices.Count && (string.CompareOrdinal(Devices[at].Kind, item.Kind) < 0 ||
                       (Devices[at].Kind == item.Kind && string.Compare(Devices[at].DisplayName, item.DisplayName, StringComparison.Ordinal) < 0))) at++;
                Devices.Insert(at, item);
            }
        }
        else if (existing is not null) { Devices.Remove(existing); Untrack(existing.Key); }
        SyncEquipment();
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

    /// <summary>Every Shooter composed: imaging trains, their guide cameras, single-camera shooters and scopes.</summary>
    public IEnumerable<string> AllShooters(bool withScopes = true)
    {
        var c = Composition;
        foreach (var t in c.Trains)
        {
            yield return t.Id.Text;
            if (t.Cameras.Any(x => x.Role.Text == "Guiding")) yield return TrainIds.GuideShooter(t.Id.Text);
        }
        foreach (var s in c.CameraShooters) yield return s.Id.Text;
        if (withScopes) foreach (var s in c.Scopes) yield return s.Id.Text;
    }

    public void Dispose()
    {
        if (!_started) return;
        foreach (var t in _tracked.Values) t.Follower?.Dispose();
        _tracked.Clear();
        try { _mesh.Node.UnhookEvent(EquipmentIds.Announce, _onAnnounce); _mesh.Node.UnhookEvent(ScopeIds.Changed, _onChanged); } catch (ObjectDisposedException) { }
    }
}
