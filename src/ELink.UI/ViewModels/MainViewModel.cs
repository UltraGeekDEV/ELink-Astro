using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>A message shown for a few seconds in the corner of the window.</summary>
public sealed record Toast(Notice Notice)
{
    public string Text => Notice.Text;
    public string Css => Notice.Css;
}

/// <summary>The window: four views (Sky, Scopes, Rig, Advanced) behind a side rail, a status strip that is always there,
/// a drawer for one device's panel, and toasts. Each view is its own view model; this only moves between them.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public MeshSession Mesh { get; }
    public CatalogViewModel Catalog { get; }
    public ComposerViewModel Composer { get; }
    public IndiBrowserViewModel IndiBrowser { get; }
    public AutofocusViewModel Autofocus { get; }
    public FocusAssistViewModel FocusAssist { get; }
    public PictureViewModel Picture { get; }
    public TonightViewModel Tonight { get; }
    public ScheduleViewModel Schedule { get; }
    public ProfilesViewModel Profiles { get; }
    public CalibrationViewModel Calibration { get; }
    public StorageViewModel Storage { get; }
    public ImageViewModel Image { get; }
    public AtlasViewModel Atlas { get; }
    public CenteringViewModel Centering { get; }
    public LiveStackViewModel LiveStack { get; }
    public SiteViewModel Site { get; }
    public EquipmentViewModel Equipment { get; }

    public SkyViewModel Sky { get; }
    public ScopesViewModel Scopes { get; }
    public RigViewModel Rig { get; }
    public AdvancedViewModel Advanced { get; }
    public StatusBarViewModel StatusBar { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Current), nameof(IsSky), nameof(IsScopes), nameof(IsPicture), nameof(IsRig), nameof(IsAdvanced))]
    private AppView _view = AppView.Sky;
    public object Current => View switch { AppView.Scopes => Scopes, AppView.Picture => Picture, AppView.Rig => Rig, AppView.Advanced => Advanced, _ => Sky };
    public bool IsSky => View == AppView.Sky;
    public bool IsScopes => View == AppView.Scopes;
    public bool IsPicture => View == AppView.Picture;
    public bool IsRig => View == AppView.Rig;
    public bool IsAdvanced => View == AppView.Advanced;

    /// <summary>One device's panel, slid in over the right edge (null = closed).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDrawer))] private DevicePanelViewModel? _drawer;
    public bool HasDrawer => Drawer is not null;
    public ObservableCollection<Toast> Toasts { get; } = new();

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
        FocusAssist = new FocusAssistViewModel(mesh, Catalog);
        Picture = new PictureViewModel(mesh);
        Schedule = new ScheduleViewModel(mesh);
        Profiles = new ProfilesViewModel(mesh);
        Calibration = new CalibrationViewModel(mesh, Catalog);
        Storage = new StorageViewModel(mesh, Catalog);
        Image = new ImageViewModel(mesh, Catalog);
        Image.Scheduler = () => Schedule;
        Atlas = new AtlasViewModel(mesh, Catalog, Image);
        Centering = new CenteringViewModel(mesh, Catalog);
        LiveStack = new LiveStackViewModel(mesh, Catalog, Image);
        Site = new SiteViewModel(mesh, Catalog, Atlas);
        Equipment = new EquipmentViewModel(mesh, Catalog, OpenDeviceAsync);

        Scopes = new ScopesViewModel(mesh, Catalog, Autofocus, Centering, FocusAssist) { OpenRig = () => Navigate(AppView.Rig, "Set up") };
        StatusBar = new StatusBarViewModel(mesh, Catalog, Scopes) { Navigate = Navigate };
        Sky = new SkyViewModel(mesh, Catalog, Atlas, Image, Schedule, LiveStack, StatusBar);
        Tonight = new TonightViewModel(mesh, Image) { OpenSite = () => Navigate(AppView.Rig, "Site") };
        Sky.Tonight = Tonight;
        Rig = new RigViewModel(Composer, Site, Equipment, Profiles, Calibration);
        Advanced = new AdvancedViewModel(IndiBrowser, Storage, LiveStack, new HelpViewModel());

        mesh.Notices.Posted += OnNotice;
        Mesh.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MeshSession.Status)) OnPropertyChanged(nameof(ConnectionStatus)); };
    }

    /// <summary>Goes to a view, and to a section of it when it has sections.</summary>
    public void Navigate(AppView view, string? section = null)
    {
        View = view;
        if (view == AppView.Rig) Rig.Show(section);
        else if (view == AppView.Advanced) Advanced.Show(section);
    }

    [RelayCommand] private void GoSky() => Navigate(AppView.Sky);
    [RelayCommand] private void GoScopes() => Navigate(AppView.Scopes);
    [RelayCommand] private void GoPicture() => Navigate(AppView.Picture);
    [RelayCommand] private void GoRig() => Navigate(AppView.Rig);
    [RelayCommand] private void GoAdvanced() => Navigate(AppView.Advanced);

    public async Task StartAsync()
    {
        await Catalog.StartAsync();
        await Autofocus.StartAsync();
        await FocusAssist.StartAsync();
        await Picture.StartAsync();
        await Tonight.StartAsync();
        await Schedule.StartAsync();
        await Profiles.StartAsync();
        await Calibration.StartAsync();
        await Storage.StartAsync();
        await Image.StartAsync();
        await Atlas.StartAsync();
        await Centering.StartAsync();
        await LiveStack.StartAsync();
        await Site.StartAsync();
        await StatusBar.StartAsync();
        await Sky.StartAsync();
        await IndiBrowser.RefreshServersAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await Mesh.ConnectAsync(Host, Port);
        if (Mesh.IsConnected) await Catalog.RefreshAsync();
    }

    [RelayCommand] private Task RefreshAsync() => Catalog.RefreshAsync();

    /// <summary>Shows a device's own panel in the drawer.</summary>
    public async Task OpenDeviceAsync(DeviceItem item)
    {
        if (Drawer is { } open && open.Kind == item.Kind && open.Id == item.Id) return;
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
        var old = Drawer;
        Drawer = panel;
        old?.Dispose();
    }

    [RelayCommand]
    private void CloseDrawer() { var old = Drawer; Drawer = null; old?.Dispose(); }

    private void OnNotice(Notice n) => UiThread.Post(() =>
    {
        var toast = new Toast(n);
        Toasts.Add(toast);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        _ = Task.Delay(n.Kind == NoticeKind.Error ? TimeSpan.FromSeconds(14) : TimeSpan.FromSeconds(6)).ContinueWith(_ => UiThread.Post(() => Toasts.Remove(toast)));
    });

    [RelayCommand] private void DismissToast(Toast? toast) { if (toast is not null) Toasts.Remove(toast); }

    public void Dispose()
    {
        Mesh.Notices.Posted -= OnNotice;
        Drawer?.Dispose();
        Scopes.Dispose(); StatusBar.Dispose();
        Catalog.Dispose(); IndiBrowser.Dispose(); Autofocus.Dispose(); FocusAssist.Dispose(); Picture.Dispose(); Tonight.Dispose(); Schedule.Dispose(); Profiles.Dispose(); Calibration.Dispose(); Storage.Dispose(); Image.Dispose(); Atlas.Dispose(); Centering.Dispose(); LiveStack.Dispose(); Site.Dispose(); Mesh.Dispose();
    }
}
