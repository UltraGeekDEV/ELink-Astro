using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Atlas;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>One suggestion: an object that is well placed tonight, and why.</summary>
public sealed record TonightItem(string Label, string Title, string Kind, string Subtitle, string Why, double Score, double RaHours, double DecDegrees, float Magnitude, float MajorArcmin, int Panels)
{
    public string ScoreText => Score.ToString("0", CultureInfo.InvariantCulture);
    /// <summary>0..1, for the bar.</summary>
    public double ScoreFraction => Math.Clamp(Score / 100, 0, 1);
}

/// <summary>Tonight's best: what the atlas thinks is worth imaging in the dark hours at your site, for your smallest frame. One click frames it.</summary>
public sealed partial class TonightViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly ImageViewModel _image;
    private Timer? _timer;
    private CancellationTokenSource? _later;
    private bool _busy;

    public TonightViewModel(MeshSession mesh, ImageViewModel image)
    {
        _mesh = mesh; _image = image;
        image.PlanChanged += () => Schedule(1500);      // another frame size: another list
    }

    /// <summary>Makes the suggestion the image to take (set by the Sky view).</summary>
    public Action<TonightItem>? Frame { get; set; }
    /// <summary>Opens the Site page (set by the main view).</summary>
    public Action? OpenSite { get; set; }

    public ObservableCollection<TonightItem> Items { get; } = new();
    [ObservableProperty] private string _window = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _needsSite;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasItems;

    public Task StartAsync()
    {
        _timer = new Timer(_ => UiThread.Post(() => Schedule(0)), null, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));
        Schedule(2500);
        return Task.CompletedTask;
    }

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
        if (_busy) return;
        _busy = true; IsBusy = true;
        try
        {
            // the smallest frame of the scopes that would take it: what an object has to fit
            double w = 0, h = 0;
            var smallest = _image.PlanScopes.SelectMany(s => s.Frames).OrderBy(f => f.HalfWidth * f.HalfHeight).Cast<ELink.Core.Astro.FrameSpec?>().FirstOrDefault();
            if (smallest is { } f) { w = 2 * f.HalfWidth; h = 2 * f.HalfHeight; }
            var answers = await _mesh.Node.CallFunctionAsync<TonightRequest, TonightList>(AtlasIds.Tonight, new TonightRequest { FovWidthDegrees = w, FovHeightDegrees = h, MaxResults = 12 }, TimeSpan.FromSeconds(60));
            var list = answers?.FirstOrDefault();
            if (list is null) { Items.Clear(); HasItems = false; NeedsSite = false; Window = ""; Message = "no sky atlas on the mesh"; return; }
            NeedsSite = !list.Ok.Value && list.Message.Text.Contains("site");
            Message = list.Ok.Value ? "" : list.Message.Text;
            Items.Clear();
            foreach (var t in list.Targets)
            {
                string title = t.CommonName.Text != "" ? $"{t.Label.Text}  ·  {t.CommonName.Text}" : t.Label.Text;
                string size = t.MajorArcmin.value >= 60 ? $"{t.MajorArcmin.value / 60:0.#}°" : $"{t.MajorArcmin.value:0}′";
                Items.Add(new TonightItem(t.Label.Text, title, t.Kind.Text, $"{Spaced(t.Kind.Text)}  ·  mag {t.Magnitude.value:0.#}  ·  {size}", t.Why.Text, t.Score.Value, t.RaHours.Value, t.DecDegrees.Value, t.Magnitude.value, t.MajorArcmin.value, t.Panels.Value));
            }
            HasItems = Items.Count > 0;
            Window = list.Ok.Value && DateTime.TryParse(list.DarkFromUtc.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from) && DateTime.TryParse(list.DarkToUtc.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var to)
                ? $"dark {from.ToLocalTime():HH:mm}–{to.ToLocalTime():HH:mm}" + (double.IsNaN(list.MoonIllumination.Value) ? "" : $"  ·  Moon {list.MoonIllumination.Value * 100:0}% lit") : "";
        }
        catch (Exception ex) { Message = "could not work it out: " + ex.Message; }
        finally { _busy = false; IsBusy = false; }
    }

    private static string Spaced(string kind) => string.Concat(kind.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? c.ToString() : c.ToString()));

    [RelayCommand] private void FrameIt(TonightItem? item) { if (item is not null) Frame?.Invoke(item); }
    [RelayCommand] private void GoToSite() => OpenSite?.Invoke();

    public void Dispose() { _later?.Cancel(); _timer?.Dispose(); }
}
