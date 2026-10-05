using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.UI.Infrastructure;
using EVent.Connections.Models.BaseBinaryConvertibles;
using ELink.Core;

namespace ELink.UI.ViewModels;

/// <summary>The picture page: the live stack, processed (gradient out, stretched, colour touches), growing as frames arrive, with each new
/// frame shown landing on it. Nothing here changes the stack: the linear data is what gets saved, the picture is a view of it.</summary>
public sealed partial class PictureViewModel : ObservableObject, IDisposable
{
    public const double HistogramWidth = 256, HistogramHeight = 64;
    public const string Automatic = "Automatic";
    private readonly MeshSession _mesh;
    private Follower<LiveStackState>? _state;
    private Action<StackFrameAdded>? _hook;
    private CancellationTokenSource? _later;
    private int _framesSeen = -1, _originalFor = -1;
    private bool _busy, _dirty;

    public PictureViewModel(MeshSession mesh)
    {
        _mesh = mesh;
        Sources.Add(Automatic);
        SelectedSource = Automatic;
    }

    /// <summary>A frame was added: the corners on the picture (0..1 of its width and height, as it is shown).</summary>
    public event Action<IReadOnlyList<Point>>? Flashed;

    public async Task StartAsync()
    {
        _state = new Follower<LiveStackState>(_mesh.Node, LiveStackIds.State, LiveStackIds.GetState, Apply);
        await _state.StartAsync();
        _hook = e => UiThread.Post(() => OnFrameAdded(e));
        await _mesh.Node.HookEventAsync(LiveStackIds.FrameAdded, _hook, "picture: new frames");
    }

    // ---- what is shown -------------------------------------------------------------------------------------------------

    public ObservableCollection<string> Sources { get; } = new();
    [ObservableProperty] private string _selectedSource = Automatic;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private Bitmap? _original;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Shown))] private bool _showOriginal;
    /// <summary>The picture, or (while the compare button is held) the same data with only the plain automatic stretch.</summary>
    public Bitmap? Shown => ShowOriginal && Original is not null ? Original : Image;
    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(Shown));
    partial void OnOriginalChanged(Bitmap? value) => OnPropertyChanged(nameof(Shown));
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _messageKind = "info";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _frames;
    [ObservableProperty] private List<Point> _histogramRed = new(), _histogramGreen = new(), _histogramBlue = new(), _histogramGrey = new();
    [ObservableProperty] private bool _colourHistogram;
    private bool _flipY = true;
    private (double Left, double Top, double Width, double Height) _crop = (0, 0, 1, 1);

    partial void OnShowOriginalChanged(bool value) { if (value && (Original is null || _originalFor != _framesSeen)) _ = RefreshOriginalAsync(); }

    private void Apply(LiveStackState s)
    {
        var names = s.Filters.Select(f => f.Text[..Math.Max(0, f.Text.LastIndexOf(':'))]).Where(n => n != "" && n != "(none)").ToList();
        var want = new[] { Automatic }.Concat(names).ToList();
        if (!Sources.SequenceEqual(want)) { Sources.Clear(); foreach (var n in want) Sources.Add(n); if (!Sources.Contains(SelectedSource)) SelectedSource = Automatic; }
        Title = s.Label.Text;
        Frames = s.FramesStacked.Value;
        if (s.FramesStacked.Value != _framesSeen) { _framesSeen = s.FramesStacked.Value; if (s.FramesStacked.Value > 0) Schedule(HasImage ? 1800 : 300); }
    }

    private void OnFrameAdded(StackFrameAdded e)
    {
        // the grid's fractions into the picture's: the picture is only the part of the grid that has data, and may be shown upside down
        Point P(double x, double y) { double ny = (y - _crop.Top) / _crop.Height; return new Point((x - _crop.Left) / _crop.Width, _flipY ? 1 - ny : ny); }
        Flashed?.Invoke([P(e.X0.Value, e.Y0.Value), P(e.X1.Value, e.Y1.Value), P(e.X2.Value, e.Y2.Value), P(e.X3.Value, e.Y3.Value)]);
    }

    // ---- the processing settings ---------------------------------------------------------------------------------------

    [ObservableProperty] private bool _stretch = true;
    [ObservableProperty] private double _backgroundLevel = 0.25;
    [ObservableProperty] private double _blackClip = 2.8;
    [ObservableProperty] private bool _linked = true;
    [ObservableProperty] private bool _removeGradient = true;
    [ObservableProperty] private double _gradientDegree = 3;
    [ObservableProperty] private bool _gradientDivide;
    [ObservableProperty] private bool _neutralize = true;
    [ObservableProperty] private double _saturation = 1.0;
    [ObservableProperty] private double _greenReduction;
    [ObservableProperty] private double _outOfFocusWeight;
    public string[] PseudoOutputs => ImageViewModel.PseudoOutputsList;
    [ObservableProperty] private string _pseudoOutput = "Colour";
    [ObservableProperty] private bool _hasPseudoMono;

    partial void OnSelectedSourceChanged(string? value) => Schedule(150);
    partial void OnStretchChanged(bool value) => Schedule(250);
    partial void OnBackgroundLevelChanged(double value) => Schedule(250);
    partial void OnBlackClipChanged(double value) => Schedule(250);
    partial void OnLinkedChanged(bool value) => Schedule(250);
    partial void OnRemoveGradientChanged(bool value) => Schedule(250);
    partial void OnGradientDegreeChanged(double value) => Schedule(300);
    partial void OnGradientDivideChanged(bool value) => Schedule(250);
    partial void OnNeutralizeChanged(bool value) => Schedule(250);
    partial void OnSaturationChanged(double value) => Schedule(250);
    partial void OnGreenReductionChanged(double value) => Schedule(250);
    partial void OnOutOfFocusWeightChanged(double value) => Schedule(300);
    partial void OnPseudoOutputChanged(string value) => Schedule(250);

    private ProcessingSettings Settings(bool plain = false) => plain
        ? new ProcessingSettings { RemoveGradient = false, NeutralizeBackground = false, Saturation = 1, GreenReduction = 0 }
        : new ProcessingSettings
        {
            Stretch = Stretch, BackgroundLevel = BackgroundLevel, BlackClipSigmas = BlackClip, Linked = Linked, RemoveGradient = RemoveGradient, GradientDegree = (int)Math.Round(GradientDegree),
            GradientDivide = GradientDivide, NeutralizeBackground = Neutralize, Saturation = Saturation, GreenReduction = GreenReduction,
        };

    private string Source => SelectedSource is null or Automatic ? "" : SelectedSource;

    // ---- presets -------------------------------------------------------------------------------------------------------

    private void Preset(double background, double clip, double saturation, double green, bool stretch = true, bool gradient = true, bool neutral = true)
    {
        Stretch = stretch; BackgroundLevel = background; BlackClip = clip; Saturation = saturation; GreenReduction = green; RemoveGradient = gradient; Neutralize = neutral; Linked = true;
    }
    /// <summary>A dark grey sky, the colours as they are.</summary>
    [RelayCommand] private void Natural() => Preset(0.25, 2.8, 1.0, 0);
    /// <summary>A darker sky, more colour: stars and nebulae stand out.</summary>
    [RelayCommand] private void Punchy() => Preset(0.18, 3.5, 1.4, 0.3);
    /// <summary>A lighter sky that shows the faintest things.</summary>
    [RelayCommand] private void Gentle() => Preset(0.36, 2.0, 1.1, 0);
    /// <summary>Nothing done to the data but scaling it: what the stack really is.</summary>
    [RelayCommand] private void Linear() => Preset(0.25, 0, 1.0, 0, stretch: false, gradient: false, neutral: false);

    // ---- making the picture --------------------------------------------------------------------------------------------

    /// <summary>After a pause (a slider still moving, frames still arriving) the picture is made again.</summary>
    private void Schedule(int delayMs)
    {
        _later?.Cancel();
        var cts = _later = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, cts.Token); } catch (OperationCanceledException) { return; }
            UiThread.Post(() => _ = RefreshAsync());
        });
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_busy) { _dirty = true; return; }
        _busy = true; IsBusy = true;
        try
        {
            do
            {
                _dirty = false;
                var request = new ProcessRequest
                {
                    Settings = Settings(), MaxWidth = 1800, MaxHeight = 1400, Source = Source, OutOfFocusWeight = OutOfFocusWeight, PseudoOutput = ImageViewModel.PseudoOutputName(PseudoOutput),
                };
                var answers = await _mesh.Node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, request, TimeSpan.FromSeconds(90));
                var a = answers?.FirstOrDefault();
                if (a is null) { Say("no picture processing on the mesh", "warn"); return; }
                if (!a.Ok.Value) { HasImage = Image is not null; if (Image is null) Say(a.Message.Text == "" ? "nothing yet" : a.Message.Text, "info"); continue; }
                var bmp = new Bitmap(new MemoryStream(a.Png.Data));
                _flipY = a.FlipY.Value; _crop = (a.CropLeft.Value, a.CropTop.Value, Math.Max(a.CropWidth.Value, 1e-6), Math.Max(a.CropHeight.Value, 1e-6));
                Image = bmp; HasImage = true;
                Say("", "info");
                Info = $"{a.Frames.Value} frame{(a.Frames.Value == 1 ? "" : "s")}  ·  {Duration(a.ExposureSeconds.Value)}  ·  {a.Width.Value}×{a.Height.Value} at {a.PixelScaleArcsec.Value:0.##}\"/px";
                Note = a.Note.Text;
                HasPseudoMono = a.Source.Text == "pseudo mono" || a.Source.Text.StartsWith("pseudo mono");
                BuildHistogram(a.Histogram.Data, a.Channels.Value);
                _originalFor = -2;                    // the plain version is made again when it is asked for
                if (ShowOriginal) await RefreshOriginalAsync();
            } while (_dirty);
        }
        catch (Exception ex) { Say("could not make the picture: " + ex.Message, "error"); }
        finally { _busy = false; IsBusy = false; }
    }

    private async Task RefreshOriginalAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest
                { Settings = Settings(plain: true), MaxWidth = 1800, MaxHeight = 1400, Source = Source, OutOfFocusWeight = OutOfFocusWeight, PseudoOutput = ImageViewModel.PseudoOutputName(PseudoOutput) }, TimeSpan.FromSeconds(90));
            if (answers?.FirstOrDefault() is { Ok.Value: true } a) { Original = new Bitmap(new MemoryStream(a.Png.Data)); _originalFor = _framesSeen; }
        }
        catch (Exception) { }
    }

    private void BuildHistogram(byte[] bins, int channels)
    {
        int n = bins.Length / Math.Max(1, channels);
        List<Point> Line(int c) => Enumerable.Range(0, n).Select(i => new Point(i * HistogramWidth / (n - 1), HistogramHeight - 2 - bins[c * n + i] / 255.0 * (HistogramHeight - 6))).ToList();
        ColourHistogram = channels == 3;
        if (channels == 3) { HistogramRed = Line(0); HistogramGreen = Line(1); HistogramBlue = Line(2); HistogramGrey = new(); }
        else { HistogramGrey = Line(0); HistogramRed = HistogramGreen = HistogramBlue = new(); }
    }

    private static string Duration(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.0#} h" : seconds >= 120 ? $"{seconds / 60:0} min" : $"{seconds:0} s";

    private void Say(string text, string kind) { MessageKind = kind; Message = text; }

    // ---- saving --------------------------------------------------------------------------------------------------------

    [ObservableProperty] private string _saved = "";

    /// <summary>The linear stack (unstretched, as it is) and the picture as it is now, saved on the station.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Say("saving…", "info");
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<ExportRequest, ExportResult>(ProcessingIds.Export, new ExportRequest
            {
                Settings = Settings(), Source = Source, Label = Title, OutOfFocusWeight = OutOfFocusWeight, PseudoOutput = ImageViewModel.PseudoOutputName(PseudoOutput),
            }, TimeSpan.FromSeconds(180));
            var r = answers?.FirstOrDefault();
            if (r is null) { Say("no picture processing on the mesh", "warn"); return; }
            if (!r.Ok.Value) { Say(r.Message.Text, "error"); return; }
            Saved = r.Message.Text; Say("", "info");
            _mesh.Notices.Success("Saved the linear stack and the picture", "Picture");
        }
        catch (Exception ex) { Say("could not save: " + ex.Message, "error"); }
    }

    [RelayCommand]
    private void Reset() { Natural(); Saturation = 1; GradientDegree = 3; GradientDivide = false; OutOfFocusWeight = 0; PseudoOutput = "Colour"; }

    public void Dispose() { _later?.Cancel(); _state?.Dispose(); if (_hook is not null) try { _mesh.Node.UnhookEvent(LiveStackIds.FrameAdded, _hook); } catch (ObjectDisposedException) { } }
}
