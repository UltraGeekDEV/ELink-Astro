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

public sealed record SavedImageItem(SavedImage Image)
{
    public string Line => $"{Image.Label.Text}   ·   {Image.Visits.Value} shots   ·   " +
        (Image.Request.TargetSeconds.Value > 0 ? $"{Math.Min(100, 100 * Image.MinSeconds.Value / Image.Request.TargetSeconds.Value):0}% deep" : $"{Image.MeanSeconds.Value / 60:0.#} min per spot") +
        $"   ·   last {(DateTime.TryParse(Image.UpdatedUtc.Text, out var t) ? t.ToLocalTime().ToString("ddd d MMM HH:mm") : "?")}";
}

/// <summary>A frame outline on the coverage map.</summary>
public sealed record OutlineShape(List<Point> Points);

/// <summary>"This image of this part of the sky": the area, how deep, which scopes may work on it. Shows the coverage
/// filling in, what every scope is doing, and the image itself as it builds. A single target is just a small area.</summary>
public sealed partial class ImageViewModel : ObservableObject, IDisposable
{
    private const double BoxWidth = 600, MaxBoxHeight = 380;
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<ImagingState>? _follower;
    private int _shownVisits = -1;

    public ImageViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildChoices);
        RebuildChoices();
        PropertyChanged += (_, e) => { if (e.PropertyName is { } n && PlanInputs.Contains(n)) SchedulePlan(); };
    }

    public ObservableCollection<ShooterChoice> Scopes { get; } = new();
    public ObservableCollection<string> Weather { get; } = new();
    public ObservableCollection<OutlineShape> Outlines { get; } = new();
    public ObservableCollection<string> Workers { get; } = new();
    public FrameDisplay Stack { get; } = new();
    public ObservableCollection<SavedImageItem> Saved { get; } = new();
    [ObservableProperty] private SavedImageItem? _selectedSaved;
    [ObservableProperty] private bool _startAfresh;

    [ObservableProperty] private string _label = "M42";
    [ObservableProperty] private string _centerRa = "05:35:17";
    [ObservableProperty] private string _centerDec = "-05:23:28";
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private double _positionAngle;
    [ObservableProperty] private double _exposureSeconds = 60;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _iso = "";
    /// <summary>Layers, one a line: name, filter, finest ″/px, coarsest ″/px, depth (minutes). Empty = one layer of everything.</summary>
    [ObservableProperty] private string _layers = "";
    [ObservableProperty] private double _targetMinutes = 60;
    [ObservableProperty] private double _stepover;
    [ObservableProperty] private double _ditherArcsec = 30;
    [ObservableProperty] private int _maxVisits;
    [ObservableProperty] private bool _liveStack = true;
    /// <summary>Save the frames of the scopes that take the image (to the folder under Advanced › Saving frames).</summary>
    [ObservableProperty] private bool _saveFrames = true;
    /// <summary>The stack is a pseudo mono one: red, green and blue from the frames where each was in focus, plus out-of-focus luminance.</summary>
    [ObservableProperty] private bool _pseudoMono;
    /// <summary>How much of the out-of-focus luminance goes into the image (0 = none).</summary>
    [ObservableProperty] private double _outOfFocusWeight;
    partial void OnOutOfFocusWeightChanged(double value) => _ = RefreshStackAsync();
    public static readonly string[] PseudoOutputsList = ["Colour", "Luminance (out of focus)", "Sharp (mono)"];
    public string[] PseudoOutputs => PseudoOutputsList;
    [ObservableProperty] private string _pseudoOutput = "Colour";
    partial void OnPseudoOutputChanged(string value) => _ = RefreshStackAsync();
    public static string PseudoOutputName(string shown) => shown.StartsWith("Lum") ? "Luminance" : shown.StartsWith("Sharp") ? "Sharp" : "Colour";
    [ObservableProperty] private double _outputScale;
    [ObservableProperty] private string? _selectedWeather = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPaused), nameof(IsActive), nameof(PhaseText), nameof(PhaseClass))]
    [NotifyCanExecuteChangedFor(nameof(StartRunCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(AbortCommand), nameof(ApplyTargetCommand))]
    private string _phase = "Idle";
    /// <summary>Working on it (or waiting for the sky or the weather to allow it).</summary>
    public bool IsRunning => Phase is "Running" or "WaitingForSky" or "WaitingForWeather";
    public bool IsPaused => Phase == "Paused";
    public bool IsActive => IsRunning || IsPaused;
    public string PhaseText => Phase switch
    {
        "Running" => "Imaging", "WaitingForSky" => "Waiting for the sky", "WaitingForWeather" => "Waiting for the weather", "Paused" => "Paused",
        "Done" => "Done", "Aborted" => "Stopped", "Error" => "Error", "Failed" => "Failed", _ => "Not started",
    };
    public string PhaseClass => Phase switch { "Running" => "busy", "Done" => "ok", "Error" or "Failed" => "error", "WaitingForSky" or "WaitingForWeather" or "Paused" => "warn", _ => "idle" };
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private string _message = "";
    /// <summary>error | ok | info: how the message is shown (an error unless said otherwise).</summary>
    [ObservableProperty] private string _messageKind = "error";
    private string _lastToast = "";
    /// <summary>Shows a message under the form; <paramref name="toast"/> also tells the person wherever they are (for what happens while they are elsewhere).</summary>
    private void Say(string text, string kind = "error", bool toast = false)
    {
        MessageKind = kind; Message = text;
        if (toast && kind == "error" && text != "" && text != _lastToast) _mesh.Notices.Error(text, "Image");
        _lastToast = text;
    }
    [ObservableProperty] private WriteableBitmap? _map;
    [ObservableProperty] private double _boxHeight = 300;
    public double BoxW => BoxWidth;
    private double _areaW = 1, _areaH = 1;

    private void RebuildChoices()
    {
        var ids = _catalog.Composition.Scopes.Select(s => s.Id.Text).ToList();
        if (!Scopes.Select(s => s.Id).SequenceEqual(ids))
        {
            var chosen = Scopes.Where(s => s.Selected).Select(s => s.Id).ToHashSet();
            Scopes.Clear();
            foreach (var id in ids)
            {
                var choice = new ShooterChoice(id) { Selected = chosen.Contains(id) || ids.Count == 1 };
                choice.PropertyChanged += (_, _) => SchedulePlan();
                Scopes.Add(choice);
            }
            SchedulePlan();
        }
        var weather = new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Weather).Select(d => d.Id)).ToList();
        if (!Weather.SequenceEqual(weather)) { Weather.Clear(); foreach (var w in weather) Weather.Add(w); }
    }

    public async Task StartAsync()
    {
        _follower = new Follower<ImagingState>(_mesh.Node, ImagingIds.State, ImagingIds.GetState, Show);
        await _follower.StartAsync();
        await RefreshSavedAsync();
    }

    /// <summary>Images started before (other nights): carry one on by loading it and pressing Start.</summary>
    [RelayCommand]
    private async Task RefreshSavedAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, NOTESVoid.Void, TimeSpan.FromSeconds(10));
            var items = (answers?.FirstOrDefault()?.Images ?? new()).Select(i => new SavedImageItem(i)).ToList();
            UiThread.Post(() => { Saved.Clear(); foreach (var i in items) Saved.Add(i); });
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private void LoadSaved()
    {
        if (SelectedSaved?.Image.Request is not { } r) return;
        Label = r.Label.Text; CenterRa = Sexagesimal.Format(r.Center.RaHours.Value, 0); CenterDec = Sexagesimal.Format(r.Center.DecDegrees.Value, 0);
        Width = r.WidthDegrees.Value; Height = r.HeightDegrees.Value; PositionAngle = r.PositionAngleDegrees.Value;
        ExposureSeconds = r.Exposure.Seconds.Value; Filter = r.Exposure.Filter.Text; Iso = r.Exposure.Iso.Text; TargetMinutes = r.TargetSeconds.Value / 60;
        Stepover = r.StepoverDegrees.Value; DitherArcsec = r.DitherArcsec.Value; OutputScale = r.OutputPixelScaleArcsec.Value; LiveStack = r.LiveStack.Value;
        foreach (var s in Scopes) s.Selected = r.ScopeIds.Any(x => x.Text == s.Id);
        Layers = string.Join("\n", r.Layers.Select(l => FormattableString.Invariant($"{l.Label.Text}, {l.Filter.Text}, {l.MinScaleArcsec.Value:0.##}, {l.MaxScaleArcsec.Value:0.##}, {l.TargetSeconds.Value / 60:0.##}")));
        StartAfresh = false;
        Say($"{r.Label.Text}: press Start to carry it on", "info");
    }

    [RelayCommand]
    private async Task DeleteSavedAsync()
    {
        if (SelectedSaved is null) return;
        var r = await Commands.CallAsync(_mesh.Node, ImagingIds.DeleteSaved, (BinaryConvertibleString)SelectedSaved.Image.Label.Text);
        Say(r.Ok.Value ? "" : r.Error.Text);
        await RefreshSavedAsync();
    }

    /// <summary>The request the form describes, or what is wrong with it (nothing is shown or posted).</summary>
    private bool TryBuild(out ImagingRequest req, out string problem)
    {
        req = new ImagingRequest(); problem = "";
        if (!Sexagesimal.TryParse(CenterRa, out var ra) || ra < 0 || ra >= 24) { problem = "centre RA must be 0..24 hours"; return false; }
        if (!Sexagesimal.TryParse(CenterDec, out var dec) || dec < -90 || dec > 90) { problem = "centre Dec must be -90..90"; return false; }
        req = new ImagingRequest
        {
            Label = Label.Trim() == "" ? "Image" : Label.Trim(), Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            WidthDegrees = Width, HeightDegrees = Height, PositionAngleDegrees = PositionAngle,
            Exposure = new ShooterExposure { Seconds = ExposureSeconds, Filter = Filter.Trim(), FrameType = "Light", Iso = Iso.Trim() },
            TargetSeconds = TargetMinutes * 60, StepoverDegrees = Stepover, DitherArcsec = DitherArcsec, MaxVisits = MaxVisits,
            WeatherId = SelectedWeather ?? "", LiveStack = LiveStack, OutputPixelScaleArcsec = OutputScale, Resume = !StartAfresh,
        };
        foreach (var line in Layers.Split('\n').Select(l => l.Trim()).Where(l => l != ""))
        {
            var p = line.Split(',').Select(x => x.Trim()).ToArray();
            double Num(int i) => i < p.Length && p[i] != "" && double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : p.Length <= i || p[i] == "" ? 0 : double.NaN;
            double min = Num(2), max = Num(3), minutes = Num(4);
            if (p[0] == "" || p.Length > 5 || double.IsNaN(min) || double.IsNaN(max) || double.IsNaN(minutes) || min < 0 || max < 0 || minutes < 0)
            { problem = $"a layer is: name, filter, finest ″/px, coarsest ″/px, depth in minutes (not '{line}')"; return false; }
            req.Layers.Add(new ImagingLayer { Label = p[0], Filter = p.Length > 1 ? p[1] : "", MinScaleArcsec = min, MaxScaleArcsec = max, TargetSeconds = minutes * 60 });
        }
        foreach (var s in Scopes.Where(s => s.Selected)) req.ScopeIds.Add(s.Id);
        if (req.ScopeIds.Count == 0) { problem = "tick at least one scope"; return false; }
        return true;
    }

    private bool TryRequest(out ImagingRequest req)
    {
        bool ok = TryBuild(out req, out var problem);
        if (!ok) Say(problem);
        return ok;
    }

    // ---- the plan, worked out by the imaging service as the form changes ----------------------------------------------

    /// <summary>One scope's frames as the plan sees them (sizes, turns and offsets of each train's field).</summary>
    public sealed record PlanScope(string ScopeId, IReadOnlyList<FrameSpec> Frames);
    public IReadOnlyList<PlanScope> PlanScopes { get; private set; } = Array.Empty<PlanScope>();
    /// <summary>Why there is no plan (empty when there is one).</summary>
    [ObservableProperty] private string _planProblem = "";
    public event Action? PlanChanged;
    private CancellationTokenSource? _planCts;

    /// <summary>Asks for a fresh plan shortly after the form stops changing.</summary>
    public void SchedulePlan()
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(350, cts.Token); await PlanAsync(cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }

    private async Task PlanAsync(CancellationToken ct)
    {
        ImagingRequest req = null!; string problem = "";
        bool ok = false;
        var tcs = new TaskCompletionSource();
        UiThread.Post(() => { ok = TryBuild(out req, out problem); tcs.SetResult(); });   // the form lives on the UI thread
        await tcs.Task;
        var scopes = new List<PlanScope>();
        if (ok)
        {
            var answers = await _mesh.Node.CallFunctionAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, req, TimeSpan.FromSeconds(15), ct);
            var p = answers?.FirstOrDefault();
            if (p is null) { ok = false; problem = "no imaging service on the mesh"; }
            else if (p.Phase.Text == "Error") { ok = false; problem = p.Message.Text; }
            else foreach (var w in p.Workers)
                scopes.Add(new PlanScope(w.ScopeId.Text, w.Frames.Select(f => new FrameSpec(f.WidthDegrees.Value / 2, f.HeightDegrees.Value / 2, f.RotationDegrees.Value, f.OffsetEastDegrees.Value, f.OffsetNorthDegrees.Value)).ToList()));
        }
        if (ct.IsCancellationRequested) return;
        UiThread.Post(() => { PlanScopes = scopes; PlanProblem = ok ? "" : problem; PlanChanged?.Invoke(); });
    }

    private static readonly HashSet<string> PlanInputs = new()
    {
        nameof(CenterRa), nameof(CenterDec), nameof(Width), nameof(Height), nameof(PositionAngle), nameof(ExposureSeconds), nameof(Stepover), nameof(Filter), nameof(Layers),
    };

    /// <summary>Where the area is and how big it is, as the imaging service last said (the map's own size).</summary>
    public double AreaWidthDegrees => _areaW;
    public double AreaHeightDegrees => _areaH;
    public bool TryCentre(out double raHours, out double decDegrees)
    {
        raHours = decDegrees = 0;
        return Sexagesimal.TryParse(CenterRa, out raHours) && Sexagesimal.TryParse(CenterDec, out decDegrees) && raHours >= 0 && raHours < 24 && Math.Abs(decDegrees) <= 90;
    }

    private string _lastPhase = "";

    private void Show(ImagingState s)
    {
        // an image that was being taken has ended: say so wherever the person is
        bool wasActive = _lastPhase is "Running" or "Paused" or "WaitingForSky" or "WaitingForWeather";
        if (wasActive && s.Phase.Text == "Done") _mesh.Notices.Success($"Image '{s.Label.Text}' is done: {s.Visits.Value} shots", "Image");
        else if (wasActive && s.Phase.Text == "Aborted") _mesh.Notices.Info($"Image '{s.Label.Text}' was stopped after {s.Visits.Value} shots; it is kept, start it again to carry on", "Image");
        _lastPhase = s.Phase.Text;
        Phase = s.Phase.Text;
        if (s.Message.Text != "") Say(s.Message.Text, s.Phase.Text == "Error" ? "error" : "info", toast: true);
        Progress = $"{s.Visits.Value} shots";
        CoverageText = s.MapCols.Value > 0
            ? $"coverage  min {Fmt(s.MinSeconds.Value)} · mean {Fmt(s.MeanSeconds.Value)} · max {Fmt(s.MaxSeconds.Value)}" +
              (s.TargetSeconds.Value > 0 ? $"   of {Fmt(s.TargetSeconds.Value)}   ({Math.Min(100, 100 * s.MinSeconds.Value / s.TargetSeconds.Value):0}% complete)" : "")
            : "";
        if (s.Layers.Count > 1 || s.Layers.Count == 1 && s.Layers[0].Label.Text != "")
            CoverageText += "\n" + string.Join("\n", s.Layers.Select(l => $"{l.Label.Text}{(l.Filter.Text != "" ? $" ({l.Filter.Text})" : "")}: {Fmt(l.MeanSeconds.Value)} mean, least {Fmt(l.MinSeconds.Value)}" +
                (l.TargetSeconds.Value > 0 ? $" of {Fmt(l.TargetSeconds.Value)}" : "") + (l.Scopes.Value == 0 ? " — no scope feeds it" : "")));
        var lines = s.Workers.Select(w => $"{w.ScopeId.Text}: {w.Phase.Text}, {w.Visits.Value} shots" + (w.Rejected.Value > 0 ? $" ({w.Rejected.Value} rejected)" : "") + (w.Message.Text != "" ? $" — {w.Message.Text}" : "")).ToList();
        if (!Workers.SequenceEqual(lines)) { Workers.Clear(); foreach (var l in lines) Workers.Add(l); }
        if (s.WidthDegrees.Value > 0) { _areaW = s.WidthDegrees.Value; _areaH = s.HeightDegrees.Value; }
        ShowMap(s);
        if (s.Visits.Value != _shownVisits && s.Visits.Value > 0) { _shownVisits = s.Visits.Value; _ = RefreshStackAsync(); }
    }

    private static string Fmt(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.0#} h" : seconds >= 120 ? $"{seconds / 60:0.#} min" : $"{seconds:0.#} s";

    private void ShowMap(ImagingState s)
    {
        int cols = s.MapCols.Value, rows = s.MapRows.Value;
        var bytes = s.Map.Data;
        if (cols <= 0 || rows <= 0 || bytes.Length != cols * rows) return;
        BoxHeight = Math.Clamp(BoxWidth * _areaH / Math.Max(_areaW, 1e-9), 60, MaxBoxHeight);
        var bgra = new byte[cols * rows * 4];
        for (int i = 0; i < bytes.Length; i++)
        {
            double t = bytes[i] / 255.0;
            byte r = (byte)Math.Clamp(20 + 235 * Math.Pow(t, 2.2), 0, 255), g = (byte)Math.Clamp(26 + 190 * Math.Pow(t, 0.9), 0, 255), b = (byte)Math.Clamp(48 + 130 * Math.Sin(t * Math.PI), 0, 255);
            bgra[i * 4] = b; bgra[i * 4 + 1] = g; bgra[i * 4 + 2] = r; bgra[i * 4 + 3] = 255;
        }
        var bmp = new WriteableBitmap(new PixelSize(cols, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
        Map = bmp;

        // where each scope is shooting now
        Outlines.Clear();
        foreach (var w in s.Workers.Where(w => w.Phase.Text == "Shooting" && !double.IsNaN(w.PoseX.Value)))
        {
            var frames = w.Frames.Select(f => new FrameSpec(f.WidthDegrees.Value / 2, f.HeightDegrees.Value / 2, f.RotationDegrees.Value, f.OffsetEastDegrees.Value, f.OffsetNorthDegrees.Value)).ToList();
            foreach (var fp in CoverageMap.Footprints(new Pose(w.PoseX.Value, w.PoseY.Value, 0), frames, PositionAngle))
            {
                double c = Math.Cos(fp.Theta), sn = Math.Sin(fp.Theta);
                var pts = new List<Point>();
                foreach (var (lx, ly) in new[] { (-fp.HalfWidth, fp.HalfHeight), (fp.HalfWidth, fp.HalfHeight), (fp.HalfWidth, -fp.HalfHeight), (-fp.HalfWidth, -fp.HalfHeight) })
                {
                    double x = fp.Cx + lx * c + ly * sn, y = fp.Cy - lx * sn + ly * c;
                    pts.Add(new Point((x + _areaW / 2) / _areaW * BoxWidth, (_areaH / 2 - y) / _areaH * BoxHeight));
                }
                Outlines.Add(new OutlineShape(pts));
            }
        }
    }

    /// <summary>The image so far, from the live stack, reduced for the screen.</summary>
    private async Task RefreshStackAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage,
                new LiveStackImageRequest { MaxWidth = 1200, MaxHeight = 900, OutOfFocusWeight = OutOfFocusWeight, PseudoOutput = PseudoOutputName(PseudoOutput) }, TimeSpan.FromSeconds(30));
            if (answers?.FirstOrDefault() is { Ok.Value: true } img)
                UiThread.Post(() => { PseudoMono = img.Filter.Text == "pseudo mono"; Stack.Show(img.Image.Data, ".fits", $"{img.Frames.Value} frames  ·  {img.Width.Value}×{img.Height.Value} at {img.PixelScaleArcsec.Value:0.##}\"/px"); });
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (!TryRequest(out var req)) return;
        var answers = await _mesh.Node.CallFunctionAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, req, TimeSpan.FromSeconds(60));
        var p = answers?.FirstOrDefault();
        if (p is null) { Say("no imaging service on the mesh"); return; }
        if (p.Phase.Text == "Error") { Say(p.Message.Text); Summary = ""; return; }
        Say("");
        Summary = p.Message.Text + "   ·   " + string.Join("   ·   ", p.Workers.Select(w =>
            $"{w.ScopeId.Text}: " + string.Join(" + ", w.Frames.Select(f => FormattableString.Invariant($"{f.WidthDegrees.Value:0.##}°×{f.HeightDegrees.Value:0.##}°")))));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartRunAsync()
    {
        if (!TryRequest(out var req)) return;
        Say("");
        _shownVisits = -1;
        string saving = await EnsureSavingAsync(req);
        var r = await Commands.CallAsync(_mesh.Node, ImagingIds.Start, req, TimeSpan.FromSeconds(240));   // (it may first learn the cameras' angles)
        if (!r.Ok.Value) Say(r.Error.Text);
        else if (saving != "") Say(saving, saving.StartsWith("Frames are NOT") ? "warn" : "info");
        await RefreshSavedAsync();
    }

    /// <summary>Frames are saved unless the person said not to: the scopes that take the image are watched by the storage service.
    /// What happened, in words ("" = nothing to say).</summary>
    private async Task<string> EnsureSavingAsync(ImagingRequest req)
    {
        if (!SaveFrames) return "Frames are NOT being saved (you turned that off in More options).";
        try
        {
            var state = (await _mesh.Node.CallFunctionAsync<NOTESVoid, ELink.Contracts.Automation.StorageState>(ELink.Contracts.Automation.StorageIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(5)))?.FirstOrDefault();
            if (state is null) return "Frames are NOT being saved: there is no frame storage on the mesh.";
            foreach (var s in req.ScopeIds)
            {
                var w = await Commands.CallAsync(_mesh.Node, ELink.Contracts.Automation.StorageIds.Watch, new ELink.Contracts.Automation.StorageWatch { ShooterId = s.Text, Enabled = true });
                if (!w.Ok.Value) return $"Frames are NOT being saved: {w.Error.Text}";
            }
            return $"Saving the frames to {state.Directory.Text}";
        }
        catch (Exception ex) { return "Frames are NOT being saved: " + ex.Message; }
    }

    private async Task Void(string id) { var r = await Commands.CallAsync(_mesh.Node, id, NOTESVoid.Void); Say(r.Ok.Value ? "" : r.Error.Text); }
    private bool CanStart() => !IsActive;
    [RelayCommand(CanExecute = nameof(IsRunning))] private Task PauseAsync() => Void(ImagingIds.Pause);
    [RelayCommand(CanExecute = nameof(IsPaused))] private Task ResumeAsync() => Void(ImagingIds.Resume);
    [RelayCommand(CanExecute = nameof(IsActive))] private Task AbortAsync() => Void(ImagingIds.Abort);

    /// <summary>The queue, to add this image to.</summary>
    public Func<ScheduleViewModel>? Scheduler { get; set; }

    [RelayCommand]
    private async Task AddToScheduleAsync()
    {
        if (Scheduler?.Invoke() is not { } schedule || !TryRequest(out var req)) return;
        string saving = await EnsureSavingAsync(req);
        await schedule.AddAsync(req);
        if (saving.StartsWith("Frames are NOT")) { Say(saving, "warn"); return; }
        if (schedule.Message != "") Say(schedule.Message); else Say($"{req.Label.Text} is in the queue", "ok");
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private async Task ApplyTargetAsync()
    {
        var r = await Commands.CallAsync(_mesh.Node, ImagingIds.SetTarget, (BinaryConvertibleDouble)(TargetMinutes * 60));
        Say(r.Ok.Value ? "" : r.Error.Text);
    }

    public void Dispose() => _follower?.Dispose();
}
