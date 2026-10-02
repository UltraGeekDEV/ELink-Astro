using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>A tab of the workspace: a view model shown with its view (picked by data template).</summary>
public sealed partial class WorkspaceTab : ObservableObject
{
    public WorkspaceTab(string title, object content, bool closable) { Title = title; Content = content; Closable = closable; }
    public string Title { get; }
    public object Content { get; }
    public bool Closable { get; }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public MeshSession Mesh { get; }
    public CatalogViewModel Catalog { get; }
    public ComposerViewModel Composer { get; }
    public IndiBrowserViewModel IndiBrowser { get; }
    public AutofocusViewModel Autofocus { get; }
    public SequencerViewModel Sequencer { get; }
    public StorageViewModel Storage { get; }
    public MosaicViewModel Mosaic { get; }
    public AtlasViewModel Atlas { get; }
    public CenteringViewModel Centering { get; }
    public LiveStackViewModel LiveStack { get; }
    public SiteViewModel Site { get; }

    public ObservableCollection<WorkspaceTab> Tabs { get; } = new();
    [ObservableProperty] private WorkspaceTab? _selectedTab;

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port = 5698;
    public string ConnectionStatus => Mesh.Status;
    public bool ShowConnectionBar { get; }

    public MainViewModel(MeshSession mesh, bool showConnectionBar = true)
    {
        Mesh = mesh;
        ShowConnectionBar = showConnectionBar;
        Catalog = new CatalogViewModel(mesh);
        Composer = new ComposerViewModel(mesh, Catalog);
        IndiBrowser = new IndiBrowserViewModel(mesh);
        Autofocus = new AutofocusViewModel(mesh, Catalog);
        Sequencer = new SequencerViewModel(mesh, Catalog);
        Storage = new StorageViewModel(mesh, Catalog);
        Mosaic = new MosaicViewModel(mesh, Catalog);
        Atlas = new AtlasViewModel(mesh, Catalog, Mosaic);
        Centering = new CenteringViewModel(mesh, Catalog);
        LiveStack = new LiveStackViewModel(mesh, Catalog, Mosaic);
        Site = new SiteViewModel(mesh, Catalog);
        Tabs.Add(new WorkspaceTab("Compose", Composer, false));
        Tabs.Add(new WorkspaceTab("Sky atlas", Atlas, false));
        Tabs.Add(new WorkspaceTab("Site", Site, false));
        Tabs.Add(new WorkspaceTab("Centring", Centering, false));
        Tabs.Add(new WorkspaceTab("Mosaic", Mosaic, false));
        Tabs.Add(new WorkspaceTab("Live stack", LiveStack, false));
        Tabs.Add(new WorkspaceTab("Sequence", Sequencer, false));
        Tabs.Add(new WorkspaceTab("Autofocus", Autofocus, false));
        Tabs.Add(new WorkspaceTab("Storage", Storage, false));
        Tabs.Add(new WorkspaceTab("INDI", IndiBrowser, false));
        SelectedTab = Tabs[0];
        Mesh.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MeshSession.Status)) OnPropertyChanged(nameof(ConnectionStatus)); };
    }

    public async Task StartAsync()
    {
        await Catalog.StartAsync();
        await Autofocus.StartAsync();
        await Sequencer.StartAsync();
        await Storage.StartAsync();
        await Mosaic.StartAsync();
        await Atlas.StartAsync();
        await Centering.StartAsync();
        await LiveStack.StartAsync();
        await Site.StartAsync();
        await IndiBrowser.RefreshServersAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await Mesh.ConnectAsync(Host.Trim(), Port);
        await Catalog.RefreshAsync();
        await IndiBrowser.RefreshServersAsync();
    }

    [RelayCommand] private Task RefreshAsync() => Catalog.RefreshAsync();

    [RelayCommand]
    private async Task OpenDeviceAsync(DeviceItem? item)
    {
        if (item is null) return;
        var existing = Tabs.FirstOrDefault(t => t.Content is DevicePanelViewModel p && p.Kind == item.Kind && p.Id == item.Id);
        if (existing is not null) { SelectedTab = existing; return; }
        DevicePanelViewModel? panel = item.Kind switch
        {
            DeviceKinds.Mount => new MountPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Camera => new CameraPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Focuser => new FocuserPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.FilterWheel => new FilterWheelPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Rotator => new RotatorPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Dome => new DomePanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Weather => new WeatherPanelViewModel(Mesh, item.Id, item.DisplayName),
            DeviceKinds.Gps => new GpsPanelViewModel(Mesh, item.Id, item.DisplayName),
            _ => null,
        };
        if (panel is null) return;
        await panel.StartAsync();
        var tab = new WorkspaceTab($"{item.Kind}: {item.DisplayName}", panel, true);
        Tabs.Add(tab); SelectedTab = tab;
    }

    [RelayCommand]
    private async Task OpenScopeAsync(ScopeItem? item)
    {
        if (item is null) return;
        var existing = Tabs.FirstOrDefault(t => t.Content is ScopePanelViewModel s && s.ScopeId == item.Id);
        if (existing is not null) { SelectedTab = existing; return; }
        string guider = Catalog.Composition.Scopes.FirstOrDefault(s => s.Id.Text == item.Id)?.GuiderId.Text ?? "";
        var panel = new ScopePanelViewModel(Mesh, item.Id, item.DisplayName, guider);
        await panel.StartAsync();
        var tab = new WorkspaceTab($"Scope: {item.DisplayName}", panel, true);
        Tabs.Add(tab); SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(WorkspaceTab? tab)
    {
        if (tab is null || !tab.Closable) return;
        int i = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        (tab.Content as IDisposable)?.Dispose();
        SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(i, Tabs.Count - 1)] : null;
    }

    public void Dispose()
    {
        foreach (var t in Tabs) (t.Content as IDisposable)?.Dispose();
        Catalog.Dispose(); IndiBrowser.Dispose(); Autofocus.Dispose(); Sequencer.Dispose(); Storage.Dispose(); Mosaic.Dispose(); Atlas.Dispose(); Centering.Dispose(); LiveStack.Dispose(); Site.Dispose(); Mesh.Dispose();
    }
}
