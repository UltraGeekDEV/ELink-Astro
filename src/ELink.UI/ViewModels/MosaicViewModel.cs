using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

/// <summary>One frame the scope takes on every visit.</summary>
public sealed partial class FrameEditor : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _width = 0.5;
    [ObservableProperty] private double _height = 0.5;
    [ObservableProperty] private double _rotation;
    [ObservableProperty] private double _offsetEast;
    [ObservableProperty] private double _offsetNorth;
}

/// <summary>A frame outline on the coverage map.</summary>
public sealed record OutlineShape(List<Point> Points);

/// <summary>Seestar-style painting of a virtual field of view: the scope keeps moving in small stepovers taking single shots until
/// every spot has received the target exposure. Shows the live coverage heat-map and controls the mosaic service by dRPC; it
/// never sees how the scope is built.</summary>
public sealed partial class MosaicViewModel : ObservableObject, IDisposable
{
    private const double BoxWidth = 600, MaxBoxHeight = 380;
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<MosaicState>? _follower;

    public MosaicViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        Frames.Add(new FrameEditor { Label = "Main", Width = 0.5, Height = 0.5 });
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Scopes { get; } = new();
    public ObservableCollection<string> Weather { get; } = new();
    public ObservableCollection<string> Cameras { get; } = new();
    public ObservableCollection<string> Rotators { get; } = new();
    public ObservableCollection<FrameEditor> Frames { get; } = new();
    public ObservableCollection<OutlineShape> Outlines { get; } = new();

    [ObservableProperty] private string? _selectedScope;
    [ObservableProperty] private string? _selectedWeather = "";
    [ObservableProperty] private string? _selectedCamera;
    [ObservableProperty] private string? _selectedRotator = "";
    [ObservableProperty] private string _label = "Mosaic";
    [ObservableProperty] private string _centerRa = "00:42:44";
    [ObservableProperty] private string _centerDec = "41:16:09";
    [ObservableProperty] private double _fovWidth = 3.0;
    [ObservableProperty] private double _fovHeight = 2.0;
    [ObservableProperty] private double _positionAngle;
    [ObservableProperty] private double _stepover = 0.05;
    [ObservableProperty] private double _exposureSeconds = 10;
    [ObservableProperty] private double _targetMinutes = 60;
    [ObservableProperty] private int _maxVisits;
    [ObservableProperty] private double _focalLength = 400;
    [ObservableProperty] private string _rotationsText = "";
    [ObservableProperty] private double _rotatorOffset;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private WriteableBitmap? _map;
    [ObservableProperty] private double _boxHeight = 260;
    public double BoxW => BoxWidth;

    private void RebuildChoices()
    {
        Fill(Scopes, _catalog.Composition.Scopes.Select(s => s.Id.Text));
        Fill(Weather, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Weather).Select(d => d.Id)));
        Fill(Cameras, _catalog.OfKind(DeviceKinds.Camera).Select(d => d.Id));
        Fill(Rotators, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Rotator).Select(d => d.Id)));
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

    [RelayCommand] private void AddFrame() => Frames.Add(new FrameEditor { Label = $"Frame {Frames.Count + 1}" });
    [RelayCommand] private void RemoveFrame(FrameEditor? f) { if (f is not null && Frames.Count > 1) Frames.Remove(f); }

    private bool TryRequest(out MosaicRequest request)
    {
        request = new MosaicRequest();
        if (SelectedScope is null) { Message = "compose a smart scope first"; return false; }
        if (!Sexagesimal.TryParse(CenterRa, out var ra) || ra < 0 || ra >= 24) { Message = "centre RA must be 0..24 hours"; return false; }
        if (!Sexagesimal.TryParse(CenterDec, out var dec) || dec < -90 || dec > 90) { Message = "centre Dec must be -90..90"; return false; }
        var rotations = new List<double>();
        foreach (var part in RotationsText.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) { Message = $"'{part}' is not an angle"; return false; }
            rotations.Add(a);
        }
        request = new MosaicRequest
        {
            Label = Label.Trim() == "" ? "Mosaic" : Label.Trim(), ScopeId = SelectedScope, WeatherId = SelectedWeather ?? "", RotatorId = SelectedRotator ?? "",
            RotatorOffsetDegrees = RotatorOffset,
            Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            FovWidthDegrees = FovWidth, FovHeightDegrees = FovHeight, PositionAngleDegrees = PositionAngle,
            StepoverDegrees = Stepover, Exposure = new ShooterExposure { Seconds = ExposureSeconds },
            TargetSeconds = TargetMinutes * 60, MaxVisits = MaxVisits,
        };
        foreach (var f in Frames)
            request.Frames.Add(new MosaicFrame { Label = f.Label, WidthDegrees = f.Width, HeightDegrees = f.Height, RotationDegrees = f.Rotation, OffsetEastDegrees = f.OffsetEast, OffsetNorthDegrees = f.OffsetNorth });
        foreach (var a in rotations) request.FieldRotations.Add(a);
        return true;
    }

    private void Apply(MosaicState s)
    {
        Phase = s.Phase.Text;
        if (s.Message.Text != "") Message = s.Message.Text;
        bool any = s.MapCols.Value > 0;
        Progress = any ? $"{s.Visits.Value} shots · " + (s.Passes.Value > 0 ? (s.Pass.Value > 0 ? $"pass {s.Pass.Value} of {s.Passes.Value}" : "topping up") + " · " : "") +
                         $"hop {(double.IsNaN(s.PassHopDegrees.Value) ? "--" : s.PassHopDegrees.Value.ToString("0.###", CultureInfo.InvariantCulture))}° · {s.QueueDepth.Value} planned ahead" : "";
        CoverageText = any ? $"coverage  min {Fmt(s.MinSeconds.Value)} · mean {Fmt(s.MeanSeconds.Value)} · max {Fmt(s.MaxSeconds.Value)}" +
                             (s.TargetSeconds.Value > 0 ? $"   of target {Fmt(s.TargetSeconds.Value)}   ({Math.Min(100, 100 * s.MinSeconds.Value / s.TargetSeconds.Value):0}% complete)" : "") : "";
        if (any) ShowMap(s);
    }

    private static string Fmt(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.0#} h" : seconds >= 120 ? $"{seconds / 60:0.#} min" : $"{seconds:0.#} s";

    private void ShowMap(MosaicState s)
    {
        int cols = s.MapCols.Value, rows = s.MapRows.Value;
        var bytes = s.Map.Data;
        if (cols <= 0 || rows <= 0 || bytes.Length != cols * rows) return;
        BoxHeight = Math.Clamp(BoxWidth * FovHeight / Math.Max(FovWidth, 1e-9), 60, MaxBoxHeight);
        var bgra = new byte[cols * rows * 4];
        for (int i = 0; i < bytes.Length; i++)
        {
            var (r, g, b) = Ramp(bytes[i] / 255.0);
            bgra[i * 4] = b; bgra[i * 4 + 1] = g; bgra[i * 4 + 2] = r; bgra[i * 4 + 3] = 255;
        }
        var bmp = new WriteableBitmap(new PixelSize(cols, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
        Map = bmp;

        Outlines.Clear();
        if (!double.IsNaN(s.PoseX.Value))
        {
            var frames = Frames.Select(f => new FrameSpec(f.Width / 2, f.Height / 2, f.Rotation, f.OffsetEast, f.OffsetNorth)).ToList();
            foreach (var fp in CoverageMap.Footprints(new Pose(s.PoseX.Value, s.PoseY.Value, s.PoseFieldAngle.Value), frames, PositionAngle))
            {
                double c = Math.Cos(fp.Theta), sn = Math.Sin(fp.Theta);
                var pts = new List<Point>();
                foreach (var (lx, ly) in new[] { (-fp.HalfWidth, fp.HalfHeight), (fp.HalfWidth, fp.HalfHeight), (fp.HalfWidth, -fp.HalfHeight), (-fp.HalfWidth, -fp.HalfHeight) })
                {
                    double x = fp.Cx + lx * c + ly * sn, y = fp.Cy - lx * sn + ly * c;
                    pts.Add(new Point((x + FovWidth / 2) / FovWidth * BoxWidth, (FovHeight / 2 - y) / FovHeight * BoxHeight));
                }
                Outlines.Add(new OutlineShape(pts));
            }
        }
    }

    /// <summary>Dark blue (nothing yet) through teal to warm yellow (target reached).</summary>
    private static (byte R, byte G, byte B) Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r = 20 + 235 * Math.Pow(t, 2.2), g = 26 + 190 * Math.Pow(t, 0.9), b = 48 + 130 * Math.Sin(t * Math.PI) ;
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (!TryRequest(out var req)) return;
        var answers = await _mesh.Node.CallFunctionAsync<MosaicRequest, MosaicPreview>(MosaicIds.Plan, req);
        var p = answers?.FirstOrDefault();
        if (p is null) { Message = "no mosaic service on the mesh"; return; }
        if (p.Error.Text != "") { Message = p.Error.Text; Summary = ""; return; }
        Message = "";
        Summary = $"hop {p.StepoverDegrees.Value:0.###}° · map {p.MapCols.Value}×{p.MapRows.Value} cells" +
                  (p.EstimatedVisits.Value > 0 ? $" · about {p.EstimatedVisits.Value} shots, {p.EstimatedHours.Value:0.#} h" : " · no target: paints until stopped");
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

    /// <summary>Change the target depth of the running scan.</summary>
    [RelayCommand]
    private async Task ApplyTargetAsync()
    {
        var r = await Commands.CallAsync(_mesh.Node, MosaicIds.SetTarget, (BinaryConvertibleDouble)(TargetMinutes * 60));
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    /// <summary>Change the stepover of the running scan.</summary>
    [RelayCommand]
    private async Task ApplyStepoverAsync()
    {
        var r = await Commands.CallAsync(_mesh.Node, MosaicIds.SetStepover, (BinaryConvertibleDouble)Stepover);
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    /// <summary>Work the first frame's field of view out from a camera's sensor and the optics' focal length.</summary>
    [RelayCommand]
    private async Task ComputeFrameAsync()
    {
        if (SelectedCamera is null) { Message = "pick a camera"; return; }
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, CameraState>(EquipmentIds.GetState(DeviceKinds.Camera, SelectedCamera), NOTESVoid.Void);
        var cam = answers?.FirstOrDefault();
        if (cam is null || cam.SensorWidth.Value <= 0 || cam.PixelSizeUm.Value <= 0) { Message = "the camera has not reported its sensor yet (connect it first)"; return; }
        var f = Frames[0];
        f.Width = Math.Round(FieldOfView.Degrees(cam.SensorWidth.Value, cam.PixelSizeUm.Value, FocalLength), 4);
        f.Height = Math.Round(FieldOfView.Degrees(cam.SensorHeight.Value, cam.PixelSizeUm.Value, FocalLength), 4);
        Message = "";
    }

    public void Dispose() => _follower?.Dispose();
}
