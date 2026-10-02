using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed record FocusRow(int Position, string Hfr, int Stars);

/// <summary>Drives the autofocus service on the mesh and shows its focus curve.</summary>
public sealed partial class AutofocusViewModel : ObservableObject, IDisposable
{
    public const double ChartWidth = 640, ChartHeight = 220;
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<AutofocusState>? _follower;

    public AutofocusViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Shooters { get; } = new();
    public ObservableCollection<string> Focusers { get; } = new();
    [ObservableProperty] private string? _selectedShooter;
    [ObservableProperty] private string? _selectedFocuser;
    [ObservableProperty] private double _exposureSeconds = 3;
    [ObservableProperty] private int _stepSize = 3000;
    [ObservableProperty] private int _samples = 7;
    [ObservableProperty] private string _filter = "";

    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _result = "";
    public ObservableCollection<FocusRow> Points { get; } = new();
    [ObservableProperty] private List<Point> _curve = new();
    [ObservableProperty] private string _chartCaption = "";

    private void RebuildChoices()
    {
        var c = _catalog.Composition;
        Fill(Shooters, _catalog.AllShooters());
        Fill(Focusers, _catalog.OfKind(DeviceKinds.Focuser).Select(d => d.Id));
        SelectedShooter ??= Shooters.FirstOrDefault(); SelectedFocuser ??= Focusers.FirstOrDefault();
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    public async Task StartAsync()
    {
        _follower = new Follower<AutofocusState>(_mesh.Node, AutofocusIds.State, AutofocusIds.GetState, Apply);
        await _follower.StartAsync();
    }

    private void Apply(AutofocusState s)
    {
        Phase = s.Phase.Text; Message = s.Message.Text;
        Points.Clear();
        foreach (var p in s.Points) Points.Add(new FocusRow(p.Position.Value, double.IsNaN(p.Hfr.Value) ? "--" : p.Hfr.Value.ToString("0.00"), p.Stars.Value));
        Result = s.Phase.Text == "Done" ? $"best focus {s.BestPosition.Value}, HFR {s.BestHfr.Value:0.00} px" : "";
        BuildChart(s);
    }

    private void BuildChart(AutofocusState s)
    {
        var pts = s.Points.Where(p => !double.IsNaN(p.Hfr.Value)).OrderBy(p => p.Position.Value).ToList();
        if (pts.Count < 2) { Curve = new List<Point>(); ChartCaption = ""; return; }
        double x0 = pts[0].Position.Value, x1 = pts[^1].Position.Value, y1 = pts.Max(p => p.Hfr.Value) * 1.1, y0 = 0;
        double sx = x1 > x0 ? (ChartWidth - 20) / (x1 - x0) : 1, sy = (ChartHeight - 20) / Math.Max(y1 - y0, 1e-6);
        Curve = pts.Select(p => new Point(10 + (p.Position.Value - x0) * sx, ChartHeight - 10 - (p.Hfr.Value - y0) * sy)).ToList();
        ChartCaption = $"HFR {pts.Min(p => p.Hfr.Value):0.0}..{pts.Max(p => p.Hfr.Value):0.0} px over focus {x0:0}..{x1:0}";
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SelectedShooter is null || SelectedFocuser is null) { Message = "pick a shooter and a focuser (compose a shooter first)"; return; }
        var r = await Commands.CallAsync(_mesh.Node, AutofocusIds.Run, new AutofocusRequest
        {
            ShooterId = SelectedShooter, FocuserId = SelectedFocuser, ExposureSeconds = ExposureSeconds, StepSize = StepSize, Samples = Samples, Filter = Filter,
        });
        if (!r.Ok.Value) Message = r.Error.Text;
    }

    [RelayCommand] private Task AbortAsync() => Commands.CallAsync(_mesh.Node, AutofocusIds.Abort, NOTESVoid.Void);

    public void Dispose() => _follower?.Dispose();
}

/// <summary>Where frames go: pick the directory, choose which shooters are saved, see what was written.</summary>
public sealed partial class StorageViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<StorageState>? _follower;
    private Action<FrameSaved>? _savedHook;

    public StorageViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Shooters { get; } = new();
    public ObservableCollection<string> Watching { get; } = new();
    public ObservableCollection<string> Recent { get; } = new();
    [ObservableProperty] private string? _selectedShooter;
    [ObservableProperty] private string _directory = "";
    [ObservableProperty] private string _directoryInput = "";
    [ObservableProperty] private int _framesSaved;
    [ObservableProperty] private string _lastFile = "";
    [ObservableProperty] private string _message = "";

    private void RebuildChoices()
    {
        var c = _catalog.Composition;
        var items = _catalog.AllShooters().ToList();
        if (!Shooters.SequenceEqual(items)) { Shooters.Clear(); foreach (var i in items) Shooters.Add(i); }
        SelectedShooter ??= Shooters.FirstOrDefault();
    }

    public async Task StartAsync()
    {
        _follower = new Follower<StorageState>(_mesh.Node, StorageIds.State, StorageIds.GetState, s =>
        {
            Directory = s.Directory.Text; if (DirectoryInput == "") DirectoryInput = s.Directory.Text;
            FramesSaved = s.FramesSaved.Value; LastFile = s.LastFile.Text; Message = s.Message.Text;
            var w = s.Watching.Select(x => x.Text).ToList();
            if (!Watching.SequenceEqual(w)) { Watching.Clear(); foreach (var i in w) Watching.Add(i); }
        });
        await _follower.StartAsync();
        _savedHook = f => UiThread.Post(() =>
        {
            Recent.Insert(0, $"{DateTime.Now:HH:mm:ss}  {System.IO.Path.GetFileName(f.Path.Text)}");
            while (Recent.Count > 30) Recent.RemoveAt(Recent.Count - 1);
        });
        await _mesh.Node.HookEventAsync(StorageIds.Saved, _savedHook, "storage log");
    }

    private async Task Run(Task<CommandResult> call) { var r = await call; Message = r.Ok.Value ? "" : r.Error.Text; }

    [RelayCommand] private Task SetDirectoryAsync() => Run(Commands.CallAsync(_mesh.Node, StorageIds.SetDirectory, (BinaryConvertibleString)DirectoryInput));
    [RelayCommand] private Task WatchAsync() => SelectedShooter is null ? Task.CompletedTask : Run(Commands.CallAsync(_mesh.Node, StorageIds.Watch, new StorageWatch { ShooterId = SelectedShooter, Enabled = true }));
    [RelayCommand] private Task UnwatchAsync(string? id) => id is null ? Task.CompletedTask : Run(Commands.CallAsync(_mesh.Node, StorageIds.Watch, new StorageWatch { ShooterId = id, Enabled = false }));

    public void Dispose()
    {
        _follower?.Dispose();
        if (_savedHook is not null) { try { _mesh.Node.UnhookEvent(StorageIds.Saved, _savedHook); } catch (ObjectDisposedException) { } }
    }
}
