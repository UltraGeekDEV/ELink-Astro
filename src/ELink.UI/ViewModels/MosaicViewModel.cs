using System.Collections.ObjectModel;
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

/// <summary>One panel on the coverage map.</summary>
public sealed partial class MosaicCell : ObservableObject
{
    public MosaicCell(int row, int col, double x, double y, double size) { Row = row; Col = col; X = x; Y = y; Size = size; }
    public int Row { get; }
    public int Col { get; }
    public double X { get; }
    public double Y { get; }
    public double Size { get; }
    [ObservableProperty] private int _frames;
    [ObservableProperty] private bool _skipped;
    [ObservableProperty] private bool _current;
    [ObservableProperty] private string _fill = "#242B38";
    public string Label => Frames > 0 ? Frames.ToString() : "";
    /// <summary>The panel being shot right now gets a bright edge.</summary>
    public string Edge => Current ? "#FFD54F" : Skipped ? "#B3372F" : "#3A4356";
    partial void OnFramesChanged(int value) => OnPropertyChanged(nameof(Label));
    partial void OnCurrentChanged(bool value) => OnPropertyChanged(nameof(Edge));
    partial void OnSkippedChanged(bool value) => OnPropertyChanged(nameof(Edge));
}

/// <summary>Seestar-style mosaic: set a virtual field of view, and the scope keeps moving over it taking single shots.
/// The panel shows the coverage map and controls the mosaic service by dRPC; it never sees how the scope is built.</summary>
public sealed partial class MosaicViewModel : ObservableObject, IDisposable
{
    private const double MapWidth = 560, MapHeight = 300;
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<MosaicState>? _follower;
    private int _rows, _cols;

    public MosaicViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Scopes { get; } = new();
    public ObservableCollection<string> Weather { get; } = new();
    public ObservableCollection<string> Cameras { get; } = new();
    public ObservableCollection<MosaicCell> Cells { get; } = new();
    public double MapW => MapWidth;
    public double MapH => MapHeight;

    [ObservableProperty] private string? _selectedScope;
    [ObservableProperty] private string? _selectedWeather = "";
    [ObservableProperty] private string? _selectedCamera;
    [ObservableProperty] private string _label = "Mosaic";
    [ObservableProperty] private string _centerRa = "00:42:44";
    [ObservableProperty] private string _centerDec = "41:16:09";
    [ObservableProperty] private double _fovWidth = 2.0;
    [ObservableProperty] private double _fovHeight = 1.0;
    [ObservableProperty] private double _positionAngle;
    [ObservableProperty] private double _frameWidth = 0.5;
    [ObservableProperty] private double _frameHeight = 0.5;
    [ObservableProperty] private double _focalLength = 400;
    [ObservableProperty] private double _overlap = 0.2;
    [ObservableProperty] private double _exposureSeconds = 10;
    [ObservableProperty] private int _passes = 20;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _progress = "";

    private void RebuildChoices()
    {
        Fill(Scopes, _catalog.Composition.Scopes.Select(s => s.Id.Text));
        Fill(Weather, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Weather).Select(d => d.Id)));
        Fill(Cameras, _catalog.OfKind(DeviceKinds.Camera).Select(d => d.Id));
        SelectedScope ??= Scopes.FirstOrDefault(); SelectedCamera ??= Cameras.FirstOrDefault();
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    public async Task StartAsync()
    {
        _follower = new Follower<MosaicState>(_mesh.Node, MosaicIds.State, MosaicIds.GetState, Apply);
        await _follower.StartAsync();
    }

    private bool TryRequest(out MosaicRequest request)
    {
        request = new MosaicRequest();
        if (SelectedScope is null) { Message = "compose a smart scope first"; return false; }
        if (!Sexagesimal.TryParse(CenterRa, out var ra) || ra < 0 || ra >= 24) { Message = "centre RA must be 0..24 hours"; return false; }
        if (!Sexagesimal.TryParse(CenterDec, out var dec) || dec < -90 || dec > 90) { Message = "centre Dec must be -90..90"; return false; }
        request = new MosaicRequest
        {
            Label = Label.Trim() == "" ? "Mosaic" : Label.Trim(), ScopeId = SelectedScope, WeatherId = SelectedWeather ?? "",
            Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            FovWidthDegrees = FovWidth, FovHeightDegrees = FovHeight, PositionAngleDegrees = PositionAngle,
            FrameWidthDegrees = FrameWidth, FrameHeightDegrees = FrameHeight, Overlap = Overlap,
            Exposure = new ShooterExposure { Seconds = ExposureSeconds }, Passes = Passes,
        };
        return true;
    }

    private void Apply(MosaicState s)
    {
        Phase = s.Phase.Text;
        Message = s.Message.Text != "" ? s.Message.Text : Message;
        Progress = s.Layout.Panels.Count == 0 ? "" :
            $"pass {s.Pass.Value}{(s.Passes.Value > 0 ? $" of {s.Passes.Value}" : "")} · {s.VisitsDone.Value} shots · {s.QueueDepth.Value} planned ahead";
        if (s.Layout.Rows.Value > 0) ShowLayout(s.Layout, s);
    }

    private void ShowLayout(MosaicLayout layout, MosaicState? state)
    {
        int rows = layout.Rows.Value, cols = layout.Cols.Value;
        if (rows != _rows || cols != _cols || Cells.Count != rows * cols)
        {
            _rows = rows; _cols = cols;
            double size = Math.Min(Math.Min(MapWidth / cols, MapHeight / rows) - 4, 90);
            double w = cols * (size + 4), h = rows * (size + 4);
            double x0 = (MapWidth - w) / 2, y0 = (MapHeight - h) / 2;
            Cells.Clear();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++) Cells.Add(new MosaicCell(r, c, x0 + c * (size + 4), y0 + r * (size + 4), size));
        }
        int max = Math.Max(1, layout.Panels.Max(p => p.Frames.Value));
        foreach (var p in layout.Panels)
        {
            var cell = Cells.FirstOrDefault(c => c.Row == p.Row.Value && c.Col == p.Col.Value);
            if (cell is null) continue;
            cell.Frames = p.Frames.Value; cell.Skipped = p.Skipped.Value;
            cell.Current = state is not null && state.CurrentRow.Value == p.Row.Value && state.CurrentCol.Value == p.Col.Value;
            double t = (double)p.Frames.Value / max;
            cell.Fill = p.Skipped.Value ? "#171B23" : p.Frames.Value == 0 ? "#242B38" : Shade(t);
        }
    }

    private static string Shade(double t)
    {
        // dark blue (few frames) to bright (many)
        int r = (int)(40 + 80 * t), g = (int)(90 + 140 * t), b = (int)(150 + 105 * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (!TryRequest(out var req)) return;
        var answers = await _mesh.Node.CallFunctionAsync<MosaicRequest, MosaicLayout>(MosaicIds.Plan, req);
        var layout = answers?.FirstOrDefault();
        if (layout is null) { Message = "no mosaic service on the mesh"; return; }
        if (layout.Error.Text != "") { Message = layout.Error.Text; Summary = ""; return; }
        Message = "";
        Summary = $"{layout.Cols.Value} × {layout.Rows.Value} = {layout.Panels.Count} panels, step {layout.StepXDegrees.Value:0.###}° × {layout.StepYDegrees.Value:0.###}°";
        _rows = 0; ShowLayout(layout, null);
    }

    [RelayCommand]
    private async Task StartRunAsync()
    {
        if (!TryRequest(out var req)) return;
        var r = await Commands.CallAsync(_mesh.Node, MosaicIds.Start, req);
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    private async Task Void(string id) { var r = await Commands.CallAsync(_mesh.Node, id, NOTESVoid.Void); Message = r.Ok.Value ? "" : r.Error.Text; }
    [RelayCommand] private Task PauseAsync() => Void(MosaicIds.Pause);
    [RelayCommand] private Task ResumeAsync() => Void(MosaicIds.Resume);
    [RelayCommand] private Task AbortAsync() => Void(MosaicIds.Abort);

    /// <summary>Change the number of rounds of the running scan (0 = until aborted).</summary>
    [RelayCommand]
    private async Task ApplyPassesAsync()
    {
        var r = await Commands.CallAsync(_mesh.Node, MosaicIds.SetPasses, (BinaryConvertibleInt32)Passes);
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    /// <summary>Click a panel to leave it out of the running scan, or back in.</summary>
    [RelayCommand]
    private async Task ToggleCellAsync(MosaicCell? cell)
    {
        if (cell is null) return;
        var r = await Commands.CallAsync(_mesh.Node, MosaicIds.SkipPanel, new PanelSkip { Row = cell.Row, Col = cell.Col, Skip = !cell.Skipped });
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    /// <summary>Work the frame field of view out from a camera's sensor and the optics' focal length.</summary>
    [RelayCommand]
    private async Task ComputeFrameAsync()
    {
        if (SelectedCamera is null) { Message = "pick a camera"; return; }
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, CameraState>(EquipmentIds.GetState(DeviceKinds.Camera, SelectedCamera), NOTESVoid.Void);
        var cam = answers?.FirstOrDefault();
        if (cam is null || cam.SensorWidth.Value <= 0 || cam.PixelSizeUm.Value <= 0) { Message = "the camera has not reported its sensor yet (connect it first)"; return; }
        FrameWidth = Math.Round(FieldOfView.Degrees(cam.SensorWidth.Value, cam.PixelSizeUm.Value, FocalLength), 4);
        FrameHeight = Math.Round(FieldOfView.Degrees(cam.SensorHeight.Value, cam.PixelSizeUm.Value, FocalLength), 4);
        Message = "";
    }

    public void Dispose() => _follower?.Dispose();
}
