using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed partial class ShooterChoice : ObservableObject
{
    public ShooterChoice(string id) { Id = id; }
    public string Id { get; }
    [ObservableProperty] private bool _selected;
}

/// <summary>Live stacking into a chosen field (by default the mosaic's virtual FOV) at a chosen pixel scale; shows the stack as it grows.</summary>
public sealed partial class LiveStackViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private readonly ImageViewModel _image;
    private Follower<LiveStackState>? _follower;
    private int _shownFrames = -1;
    private bool _fetching, _dirty;

    public LiveStackViewModel(MeshSession mesh, CatalogViewModel catalog, ImageViewModel image)
    {
        _mesh = mesh; _catalog = catalog; _image = image;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<ShooterChoice> Shooters { get; } = new();
    public string[] Interpolations { get; } = ["Bicubic", "Bilinear", "Nearest"];
    public string[] Registrations { get; } = ["Auto", "Solve", "Pointing"];
    public string[] DebayerModes { get; } = ["Interpolated", "SuperPixel", "None"];
    public string[] BayerPatterns { get; } = ["From frame", "RGGB", "BGGR", "GRBG", "GBRG"];
    public FrameDisplay Preview { get; } = new();

    [ObservableProperty] private string _label = "Live stack";
    [ObservableProperty] private string _centerRa = "05:35:17";
    [ObservableProperty] private string _centerDec = "-05:23:28";
    [ObservableProperty] private double _fovWidth = 1.5;
    [ObservableProperty] private double _fovHeight = 1.0;
    [ObservableProperty] private double _positionAngle;
    [ObservableProperty] private double _pixelScale = 3;
    [ObservableProperty] private string _interpolation = "Bicubic";
    [ObservableProperty] private string _registration = "Auto";
    [ObservableProperty] private double _framePixelScale;
    [ObservableProperty] private double _framePositionAngle;
    [ObservableProperty] private bool _normalizeBackground = true;
    [ObservableProperty] private double _maxMegapixels = 40;
    [ObservableProperty] private string _debayer = "Interpolated";
    [ObservableProperty] private bool _separateFilters = true;
    [ObservableProperty] private bool _matchFlux = true;
    [ObservableProperty] private bool _calibrate = true;
    [ObservableProperty] private double _rejectSigma = 3;
    [ObservableProperty] private bool _neutralize = true;
    [ObservableProperty] private string? _shownFilter;
    public ObservableCollection<string> StackFilters { get; } = new();
    partial void OnShownFilterChanged(string? value) => _ = RefreshAsync();
    partial void OnNeutralizeChanged(bool value) => _ = RefreshAsync();
    [ObservableProperty] private string _bayerPattern = "From frame";

    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _stateMessage = "";
    [ObservableProperty] private string _message = "";

    /// <summary>Output size at the chosen scale, as a hint while editing.</summary>
    public string SizeText => PixelScale > 0
        ? $"{FovWidth * 3600 / PixelScale:0} × {FovHeight * 3600 / PixelScale:0} px  ({FovWidth * FovHeight * 3600 * 3600 / PixelScale / PixelScale / 1e6:0.#} MP)"
        : "size follows the first frame's scale";
    partial void OnPixelScaleChanged(double value) => OnPropertyChanged(nameof(SizeText));
    partial void OnFovWidthChanged(double value) => OnPropertyChanged(nameof(SizeText));
    partial void OnFovHeightChanged(double value) => OnPropertyChanged(nameof(SizeText));

    private void RebuildChoices()
    {
        var c = _catalog.Composition;
        var ids = _catalog.AllShooters().ToList();
        if (Shooters.Select(s => s.Id).SequenceEqual(ids)) return;
        var selected = Shooters.Where(s => s.Selected).Select(s => s.Id).ToHashSet();
        Shooters.Clear();
        foreach (var id in ids) Shooters.Add(new ShooterChoice(id) { Selected = selected.Contains(id) });
    }

    public async Task StartAsync()
    {
        _follower = new Follower<LiveStackState>(_mesh.Node, LiveStackIds.State, LiveStackIds.GetState, s =>
        {
            Phase = s.Phase.Text; StateMessage = s.Message.Text;
            Summary = s.Width.Value == 0 && s.FramesStacked.Value == 0
                ? (s.Phase.Text == "Idle" ? "" : "waiting for frames")
                : $"{s.FramesStacked.Value} frames ({s.TotalExposureSeconds.Value / 60:0.#} min)   ·   {s.FramesRejected.Value} rejected   ·   {s.FramesPending.Value} queued   ·   " +
                  $"{s.Width.Value}×{s.Height.Value} {(s.Channels.Value == 3 ? "RGB" : "mono")} at {s.PixelScaleArcsec.Value:0.##}\"/px   ·   {s.CoveragePercent.Value:0}% covered";
            var filters = s.Filters.Select(f => f.Text[..Math.Max(0, f.Text.LastIndexOf(':'))]).ToList();
            if (!StackFilters.SequenceEqual(filters)) { StackFilters.Clear(); foreach (var f in filters) StackFilters.Add(f); }
            if (s.FramesStacked.Value != _shownFrames) { _shownFrames = s.FramesStacked.Value; _ = RefreshAsync(); }
        });
        await _follower.StartAsync();
    }

    [RelayCommand]
    private void FromImage()
    {
        CenterRa = _image.CenterRa; CenterDec = _image.CenterDec;
        if (_image.Width > 0) FovWidth = _image.Width;
        if (_image.Height > 0) FovHeight = _image.Height;
        PositionAngle = _image.PositionAngle; Label = _image.Label;
        foreach (var s in Shooters) if (_image.Scopes.Any(c => c.Selected && c.Id == s.Id)) s.Selected = true;
        Message = "";
    }

    [RelayCommand]
    private async Task StartStackAsync()
    {
        if (!Sexagesimal.TryParse(CenterRa, out var ra) || ra < 0 || ra >= 24) { Message = "centre RA must be 0..24 hours"; return; }
        if (!Sexagesimal.TryParse(CenterDec, out var dec) || dec < -90 || dec > 90) { Message = "centre Dec must be -90..90"; return; }
        var r = new LiveStackRequest
        {
            Label = Label.Trim() == "" ? "Live stack" : Label.Trim(), Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            FovWidthDegrees = FovWidth, FovHeightDegrees = FovHeight, PositionAngleDegrees = PositionAngle, PixelScaleArcsec = PixelScale,
            Interpolation = Interpolation, Registration = Registration, FramePixelScaleArcsec = FramePixelScale, FramePositionAngleDegrees = FramePositionAngle,
            NormalizeBackground = NormalizeBackground, MaxMegapixels = MaxMegapixels,
            Debayer = Debayer, BayerPattern = BayerPattern == "From frame" ? "" : BayerPattern,
            SeparateFilters = SeparateFilters, MatchFlux = MatchFlux, RejectSigma = RejectSigma, Calibrate = Calibrate,
        };
        foreach (var s in Shooters.Where(s => s.Selected)) r.ShooterIds.Add(s.Id);
        if (r.ShooterIds.Count == 0) { Message = "tick at least one shooter or scope"; return; }
        var result = await Commands.CallAsync(_mesh.Node, LiveStackIds.Start, r);
        Message = result.Ok.Value ? "" : result.Error.Text;
        if (result.Ok.Value) { _shownFrames = 0; Preview.Image = null; Preview.Info = "no frame yet"; }
    }

    [RelayCommand] private async Task StopAsync() { var r = await Commands.CallAsync(_mesh.Node, LiveStackIds.Stop, NOTESVoid.Void); Message = r.Ok.Value ? "" : r.Error.Text; }
    [RelayCommand] private async Task ResetAsync() { var r = await Commands.CallAsync(_mesh.Node, LiveStackIds.Reset, NOTESVoid.Void); Message = r.Ok.Value ? "" : r.Error.Text; }

    /// <summary>Fetches a screen-sized (area-averaged) copy of the stack; at most one request in flight.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_fetching) { _dirty = true; return; }
        _fetching = true;
        try
        {
            do
            {
                _dirty = false;
                var answers = await _mesh.Node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage,
                    new LiveStackImageRequest { MaxWidth = 1600, MaxHeight = 1200, Neutralize = Neutralize, Filter = ShownFilter is null or "(none)" ? "" : ShownFilter }, TimeSpan.FromSeconds(30));
                if (answers?.FirstOrDefault() is { Ok.Value: true } img)
                    UiThread.Post(() => Preview.Show(img.Image.Data, ".fits", $"{(img.Filter.Text != "" ? img.Filter.Text + ": " : "")}{img.Frames.Value} frames  ·  shown at {img.PixelScaleArcsec.Value:0.##}\"/px"));
            } while (_dirty);
        }
        catch (Exception ex) { Message = "cannot fetch the stack: " + ex.Message; }
        finally { _fetching = false; }
    }

    public void Dispose() => _follower?.Dispose();
}
