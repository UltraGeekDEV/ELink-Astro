using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>A helper for focusing by hand: it takes short frames over and over and shows how sharp the stars are, so the numbers
/// can be watched while the focus knob is turned. It can be left open.</summary>
public sealed partial class FocusAssistViewModel : ObservableObject, IDisposable
{
    public const double ChartWidth = 640, ChartHeight = 170;
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<FocusAssistState>? _follower;

    public FocusAssistViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Shooters { get; } = new();
    [ObservableProperty] private string? _selectedShooter;
    [ObservableProperty] private double _exposureSeconds = 2;
    [ObservableProperty] private string _filter = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning))] [NotifyCanExecuteChangedFor(nameof(BeginCommand), nameof(StopCommand))] private string _phase = "Idle";
    public bool IsRunning => Phase == "Running";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _hfrText = "--";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _colourText = "";
    [ObservableProperty] private string _bestText = "";
    [ObservableProperty] private string _trendText = "";
    [ObservableProperty] private string _trendKind = "info";
    [ObservableProperty] private List<Point> _hfrLine = new();
    [ObservableProperty] private List<Point> _redLine = new(), _greenLine = new(), _blueLine = new();
    [ObservableProperty] private bool _hasColour;

    private void RebuildChoices()
    {
        var ids = _catalog.AllShooters(withScopes: false).ToList();
        if (!Shooters.SequenceEqual(ids)) { Shooters.Clear(); foreach (var i in ids) Shooters.Add(i); }
        SelectedShooter ??= Shooters.FirstOrDefault();
    }

    public async Task StartAsync()
    {
        _follower = new Follower<FocusAssistState>(_mesh.Node, FocusAssistIds.State, FocusAssistIds.GetState, Apply);
        await _follower.StartAsync();
    }

    private static string F(double v) => double.IsNaN(v) ? "--" : v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void Apply(FocusAssistState s)
    {
        Phase = s.Phase.Text; Message = s.Message.Text;
        var last = s.Samples.Count > 0 ? s.Samples[^1] : null;
        HfrText = last is null ? "--" : F(last.Hfr.Value);
        DetailText = last is null ? "" : $"{last.Stars.Value} stars" + (double.IsNaN(last.Elongation.Value) ? "" : $" · roundness {F(last.Elongation.Value)}") + $" · {s.Frames.Value} frames";
        HasColour = last is not null && !double.IsNaN(last.HfrGreen.Value);
        ColourText = HasColour ? $"red {F(last!.HfrRed.Value)} · green {F(last.HfrGreen.Value)} · blue {F(last.HfrBlue.Value)}" : "";
        BestText = double.IsNaN(s.BestHfr.Value) ? "" : $"sharpest so far {F(s.BestHfr.Value)} px";
        (TrendText, TrendKind) = s.Trend.Text switch { "sharper" => ("getting sharper", "ok"), "softer" => ("getting softer", "warn"), "steady" => ("steady", "info"), _ => ("", "info") };
        var all = s.Samples.SelectMany(x => new[] { x.Hfr.Value, x.HfrRed.Value, x.HfrGreen.Value, x.HfrBlue.Value }).Where(v => !double.IsNaN(v)).ToList();
        double top = all.Count > 0 ? all.Max() * 1.1 : 1;
        List<Point> Line(Func<FocusAssistSample, double> pick)
        {
            var pts = new List<Point>();
            int n = s.Samples.Count;
            for (int i = 0; i < n; i++)
            {
                double v = pick(s.Samples[i]);
                if (double.IsNaN(v)) continue;
                pts.Add(new Point(10 + (n <= 1 ? 0 : i * (ChartWidth - 20) / (n - 1)), ChartHeight - 10 - v / Math.Max(top, 1e-6) * (ChartHeight - 20)));
            }
            return pts.Count >= 2 ? pts : new List<Point>();
        }
        HfrLine = Line(x => x.Hfr.Value);
        RedLine = Line(x => x.HfrRed.Value); GreenLine = Line(x => x.HfrGreen.Value); BlueLine = Line(x => x.HfrBlue.Value);
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task BeginAsync()
    {
        if (SelectedShooter is null) { Message = "pick a camera (set up a telescope first)"; return; }
        var r = await Commands.CallAsync(_mesh.Node, FocusAssistIds.Start, new FocusAssistRequest { ShooterId = SelectedShooter, ExposureSeconds = ExposureSeconds, Filter = Filter.Trim() });
        if (!r.Ok.Value) Message = r.Error.Text;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))] private Task StopAsync() => Commands.CallAsync(_mesh.Node, FocusAssistIds.Stop, NOTESVoid.Void);
    [RelayCommand] private Task ResetAsync() => Commands.CallAsync(_mesh.Node, FocusAssistIds.Reset, NOTESVoid.Void);

    public void Dispose() => _follower?.Dispose();
}
