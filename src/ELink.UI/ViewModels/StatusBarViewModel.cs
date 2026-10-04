using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public enum AppView { Sky, Scopes, Rig, Advanced }

/// <summary>One thing that has to be in place before a night's imaging can start, and where to fix it.</summary>
public sealed partial class ReadinessItem : ObservableObject
{
    public ReadinessItem(string title, string todo, AppView view, string section) { Title = title; Todo = todo; View = view; Section = section; }
    public string Title { get; }
    /// <summary>What to do when it is not done.</summary>
    public string Todo { get; }
    public AppView View { get; }
    public string Section { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text)), NotifyPropertyChangedFor(nameof(Mark))] private bool _done;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text))] private string _detail = "";
    public string Mark => Done ? "✓" : "○";
    public string Text => Done ? (Detail != "" ? $"{Title}: {Detail}" : Title) : $"{Title}: {Todo}";
}

/// <summary>The strip along the top of the window that is always there: how dark it is, what each scope is doing, and
/// whether anything is still to set up.</summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<SiteState>? _site;

    public StatusBarViewModel(MeshSession mesh, CatalogViewModel catalog, ScopesViewModel scopes)
    {
        _mesh = mesh; _catalog = catalog; Scopes = scopes;
        Readiness = new ObservableCollection<ReadinessItem>
        {
            new("Equipment connected", "connect your mount and camera (Rig › Equipment)", AppView.Rig, "Equipment"),
            new("Scope set up", "define a scope (Rig › Set up)", AppView.Rig, "Set up"),
            new("Site known", "enter your location (Rig › Site)", AppView.Rig, "Site"),
            new("Plate solver", "install ASTAP or astrometry.net", AppView.Rig, "Drivers"),
        };
        mesh.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MeshSession.IsConnected) or nameof(MeshSession.Status)) UiThread.Post(() => { OnPropertyChanged(nameof(NotConnected)); OnPropertyChanged(nameof(MeshText)); }); };
        _equipmentWatch = (_, _) => UiThread.Post(Recompute);
        catalog.Equipment.CollectionChanged += _equipmentWatch;
        catalog.CompositionChanged += () => UiThread.Post(Recompute);
        catalog.Equipment.CollectionChanged += (_, _) => WatchItems();
        WatchItems();
    }

    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _equipmentWatch;
    private readonly HashSet<DeviceItem> _watched = new();

    /// <summary>Re-check readiness whenever a device connects or disconnects.</summary>
    private void WatchItems()
    {
        foreach (var d in _catalog.Equipment.Where(d => _watched.Add(d)))
            d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DeviceItem.Connected)) UiThread.Post(Recompute); };
    }

    public ScopesViewModel Scopes { get; }
    public ObservableCollection<ReadinessItem> Readiness { get; }
    /// <summary>Set by the shell: go to a view (and a section of it).</summary>
    public Action<AppView, string?>? Navigate { get; set; }

    /// <summary>Shown first when this window is not joined to a station.</summary>
    public bool NotConnected => !_mesh.IsConnected;
    public string MeshText => _mesh.IsConnected ? "" : _mesh.Status.StartsWith("not connected", StringComparison.OrdinalIgnoreCase) ? "Not connected to a station" : "Not connected: " + _mesh.Status;
    [ObservableProperty] private string _nightText = "";
    [ObservableProperty] private string _nightClass = "idle";
    [ObservableProperty] private string _moonText = "";
    [ObservableProperty] private string _setupText = "";
    [ObservableProperty] private string _setupClass = "idle";
    [ObservableProperty] private bool _needsSetup;
    private SiteState? _siteState;
    private bool _solver;

    private static string Local(string iso) =>
        iso == "" ? "" : DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("HH:mm");

    public async Task StartAsync()
    {
        _site = new Follower<SiteState>(_mesh.Node, SiteIds.State(SiteIds.Default), SiteIds.GetState(SiteIds.Default), s => { _siteState = s; Recompute(); });
        await _site.StartAsync();
        await RefreshSolversAsync();
        Recompute();
    }

    /// <summary>Which plate solvers the mesh offers (asked once, and again when asked to).</summary>
    public async Task RefreshSolversAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, BinaryConvertibleString>(SolveIds.Solvers, NOTESVoid.Void, TimeSpan.FromSeconds(5));
            var list = answers?.FirstOrDefault()?.Text ?? "";
            UiThread.Post(() => { _solver = list != ""; Readiness[3].Detail = list.Replace(",", " and "); Recompute(); });
        }
        catch (Exception) { }
    }

    private void Recompute()
    {
        var site = _siteState;
        if (site is not { Known.Value: true }) { NightText = "Site not set"; NightClass = "warn"; MoonText = ""; }
        else
        {
            string sky = site.Sky.Text switch
            {
                "Day" => "Daylight", "CivilTwilight" => "Civil twilight", "NauticalTwilight" => "Nautical twilight", "AstronomicalTwilight" => "Astronomical twilight", _ => "Dark",
            };
            NightClass = site.Sky.Text == "Night" ? "ok" : site.Sky.Text == "Day" ? "idle" : "busy";
            NightText = site.Sky.Text == "Night" ? $"{sky} until {Local(site.DawnUtc.Text)}"
                : site.Sky.Text == "Day" && site.DuskUtc.Text != "" ? $"{sky}, dark from {Local(site.DuskUtc.Text)}"
                : sky;
            MoonText = double.IsNaN(site.MoonIllumination.Value) ? "" : $"Moon {site.MoonIllumination.Value * 100:0}%" + (site.MoonAltitude.Value > 0 ? "" : " (down)");
        }
        var devices = _catalog.Equipment;
        bool mount = devices.Any(d => d.Kind == DeviceKinds.Mount && d.Connected), camera = devices.Any(d => d.Kind == DeviceKinds.Camera && d.Connected);
        Readiness[0].Done = mount && camera;
        Readiness[0].Detail = "mount and camera";
        Readiness[1].Done = _catalog.Composition.Trains.Count > 0 && _catalog.Composition.Scopes.Count > 0;
        Readiness[1].Detail = $"{_catalog.Composition.Scopes.Count} scope{(_catalog.Composition.Scopes.Count == 1 ? "" : "s")}";
        Readiness[2].Done = site is { Known.Value: true };
        Readiness[2].Detail = site is { Known.Value: true } ? site.Config.Label.Text : "";
        Readiness[3].Done = _solver;
        int missing = Readiness.Count(r => !r.Done);
        NeedsSetup = missing > 0;
        SetupText = missing == 0 ? "Ready" : $"{missing} thing{(missing == 1 ? "" : "s")} to set up";
        SetupClass = missing == 0 ? "ok" : "warn";
    }

    [RelayCommand] private void Fix(ReadinessItem? item) { if (item is not null) Navigate?.Invoke(item.View, item.Section); }
    [RelayCommand] private void OpenScope(ScopePanelViewModel? card) { if (card is null) return; Scopes.Selected = card; Navigate?.Invoke(AppView.Scopes, null); }
    [RelayCommand] private void OpenSite() => Navigate?.Invoke(AppView.Rig, "Site");

    public void Dispose() { _site?.Dispose(); _catalog.Equipment.CollectionChanged -= _equipmentWatch; }
}
