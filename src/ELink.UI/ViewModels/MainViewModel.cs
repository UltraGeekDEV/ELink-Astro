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
        Tabs.Add(new WorkspaceTab("Compose", Composer, false));
        Tabs.Add(new WorkspaceTab("INDI", IndiBrowser, false));
        SelectedTab = Tabs[0];
        Mesh.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MeshSession.Status)) OnPropertyChanged(nameof(ConnectionStatus)); };
    }

    public async Task StartAsync()
    {
        await Catalog.StartAsync();
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
        var panel = new ScopePanelViewModel(Mesh, item.Id, item.DisplayName);
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
        Catalog.Dispose(); IndiBrowser.Dispose(); Mesh.Dispose();
    }
}
