using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Atlas;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Controls;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed record AtlasHitItem(string Label, string Kind, string Detail, double RaHours, double DecDegrees, float Magnitude, float MajorArcmin)
{
    public string Line => $"{Label}   {Kind}" + (float.IsNaN(Magnitude) ? "" : $"   mag {Magnitude:0.#}") + (Detail != "" ? $"   {Detail}" : "");
}

/// <summary>The sky atlas: an interactive chart of the atlas service's stars, deep-sky objects and constellations, with the mounts
/// and smart scopes drawn where they point, the requested image outlined, search, and point-and-go. Stellarium can be driven from here
/// too. Knows the backend only by EVent IDs.</summary>
public sealed partial class AtlasViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private readonly ImageViewModel? _image;
    private readonly Dictionary<string, IDisposable> _followers = new();
    private readonly Dictionary<string, ChartMarker> _mounts = new();
    private Follower<StellariumState>? _stellarium;
    private Action<AtlasHit>? _stellariumSelected;
    private CancellationTokenSource? _pending;
    private int _queries;
    private Follower<SiteState>? _site;
    private SiteState? _siteState;
    private Timer? _skyTimer;
    private List<SkyBody> _bodyList = new();

    public AtlasViewModel(MeshSession mesh, CatalogViewModel catalog, ImageViewModel? image = null)
    {
        _mesh = mesh; _catalog = catalog; _image = image;
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(() => _ = FollowEquipmentAsync());
        catalog.CompositionChanged += () => UiThread.Post(() => { RebuildPointers(); _ = FollowEquipmentAsync(); });
        RebuildPointers();
    }

    // view
    [ObservableProperty] private double _centerRa = 5.6;
    [ObservableProperty] private double _centerDec = 0;
    [ObservableProperty] private double _fov = 60;
    [ObservableProperty] private double _starLimit = 6.5;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showConstellations = true;
    // what the chart draws
    [ObservableProperty] private IReadOnlyList<ChartStar> _stars = Array.Empty<ChartStar>();
    [ObservableProperty] private IReadOnlyList<ChartDso> _dsos = Array.Empty<ChartDso>();
    [ObservableProperty] private IReadOnlyList<ChartSegment> _lines = Array.Empty<ChartSegment>();
    [ObservableProperty] private IReadOnlyList<ChartLabel> _labels = Array.Empty<ChartLabel>();
    [ObservableProperty] private IReadOnlyList<ChartMarker> _markers = Array.Empty<ChartMarker>();
    [ObservableProperty] private IReadOnlyList<ChartPolygon> _polygons = Array.Empty<ChartPolygon>();
    [ObservableProperty] private ChartHorizon? _horizon;
    [ObservableProperty] private IReadOnlyList<ChartBody> _bodies = Array.Empty<ChartBody>();
    [ObservableProperty] private bool _showHorizon = true;
    [ObservableProperty] private string _visibilityText = "";
    // search and selection
    [ObservableProperty] private string _searchText = "";
    public ObservableCollection<AtlasHitItem> Results { get; } = new();
    /// <summary>The list of matches under the search box is shown until one is picked or it is dismissed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResults))] private bool _resultsOpen;
    public bool HasResults => ResultsOpen && Results.Count > 0;
    [RelayCommand] private void CloseResults() => ResultsOpen = false;
    [ObservableProperty] private AtlasHitItem? _selectedResult;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(GotoCommand), nameof(ImageThisCommand), nameof(ShowInStellariumCommand))] private AtlasHitItem? _selection;
    [ObservableProperty] private string _selectionText = "Click the chart or search to select an object.";
    // acting on the selection
    public ObservableCollection<string> Pointers { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(GotoCommand))] private string? _selectedPointer;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _messageKind = "error";
    private void Say(string text, string kind = "error") { MessageKind = kind; Message = text; }
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _stellariumText = "Stellarium: not connected";
    [ObservableProperty] private string _stellariumOffer = "";

    public int QueriesMade => _queries;

    public async Task StartAsync()
    {
        try
        {
            var sets = await _mesh.Node.CallFunctionAsync<NOTESVoid, ConstellationSet>(AtlasIds.Constellations, NOTESVoid.Void);
            var set = sets?.FirstOrDefault();
            if (set is not null)
            {
                var lines = set.Lines.Select(l => new ChartSegment(l.Ra1Hours.value, l.Dec1Degrees.value, l.Ra2Hours.value, l.Dec2Degrees.value)).ToList();
                var labels = set.Labels.Select(l => new ChartLabel(l.Label.Text, l.RaHours.value, l.DecDegrees.value)).ToList();
                UiThread.Post(() => { Lines = lines; Labels = labels; });
            }
            else UiThread.Post(() => Status = "no atlas service on the mesh");
        }
        catch (Exception ex) { UiThread.Post(() => Status = "atlas: " + ex.Message); }

        _stellarium = new Follower<StellariumState>(_mesh.Node, StellariumIds.State, StellariumIds.GetState, s =>
        {
            StellariumText = $"Stellarium: telescope port {s.TelescopePort.Value} ({s.TelescopeClients.Value} connected" +
                             (s.PointerId.Text != "" ? $", showing {s.PointerId.Text}" : "") + ") · remote control " + (s.RemoteReachable.Value ? "reachable" : "not reachable") +
                             (s.Message.Text != "" ? " · " + s.Message.Text : "");
        });
        await _stellarium.StartAsync();
        _stellariumSelected = hit => UiThread.Post(() => StellariumOffer = hit.Label.Text != "" ? $"Stellarium selected {hit.Label.Text}" : "");
        await _mesh.Node.HookEventAsync(StellariumIds.Selected, _stellariumSelected, "atlas: Stellarium selection");

        _site = new Follower<SiteState>(_mesh.Node, SiteIds.State(SiteIds.Default), SiteIds.GetState(SiteIds.Default), s =>
        {
            _siteState = s;
            RebuildHorizon();
            _ = RefreshBodiesAsync();
        });
        await _site.StartAsync();
        _skyTimer = new Timer(_ => UiThread.Post(() => { RebuildHorizon(); _ = RefreshBodiesAsync(); }), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        await FollowEquipmentAsync();
        UpdateOverlays();
        await RefreshBodiesAsync();
        await RefreshAsync();
    }

    // ---- the site: horizon, Sun, Moon and planets ------------------------------------------------------------------

    private GeoSite? Site => _siteState is { Known.Value: true } s
        ? new GeoSite(s.Config.LatitudeDegrees.Value, s.Config.LongitudeDegrees.Value, s.Config.ElevationMeters.Value) : null;

    partial void OnShowHorizonChanged(bool value) => RebuildHorizon();

    /// <summary>The site's horizon (flat minimum and profile) as it lies on the sky right now.</summary>
    private void RebuildHorizon()
    {
        if (!ShowHorizon || Site is not { } site || _siteState is not { } st) { Horizon = null; return; }
        var now = DateTime.UtcNow;
        var profile = st.Config.Horizon.Select(p => (p.AzimuthDegrees.Value, p.AltitudeDegrees.Value)).ToList();
        (double, double) J2000(double alt, double az)
        {
            var (ra, dec) = ELink.Core.Astro.Horizon.FromAltAz(alt, az, now, site);
            return Precession.DateToJ2000(ra, dec, now);
        }
        var line = Enumerable.Range(0, 361).Select(az => J2000(ELink.Core.Astro.Horizon.ProfileAltitude(st.Config.MinAltitudeDegrees.Value, profile, az), az)).ToList();
        var cardinals = new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) }
            .Select(c => { var (ra, dec) = J2000(ELink.Core.Astro.Horizon.ProfileAltitude(st.Config.MinAltitudeDegrees.Value, profile, c.Item2), c.Item2); return new ChartLabel(c.Item1, (float)ra, (float)dec); }).ToList();
        Horizon = new ChartHorizon(line, cardinals);
    }

    private static readonly Dictionary<string, (Color Color, double Radius)> BodyLook = new()
    {
        ["Sun"] = (Color.FromRgb(255, 220, 90), 0.267), ["Moon"] = (Color.FromRgb(225, 225, 210), 0.259),
        ["Mercury"] = (Color.FromRgb(200, 190, 180), 0), ["Venus"] = (Color.FromRgb(255, 250, 220), 0), ["Mars"] = (Color.FromRgb(255, 130, 90), 0),
        ["Jupiter"] = (Color.FromRgb(240, 215, 170), 0), ["Saturn"] = (Color.FromRgb(230, 210, 140), 0),
        ["Uranus"] = (Color.FromRgb(170, 230, 240), 0), ["Neptune"] = (Color.FromRgb(120, 150, 255), 0),
    };

    private async Task RefreshBodiesAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, SkyBodies>(SiteIds.Bodies(SiteIds.Default), NOTESVoid.Void, TimeSpan.FromSeconds(10));
            if (answers?.FirstOrDefault() is not { } b) return;
            var list = b.Bodies.ToList();
            var chart = list.Select(x =>
            {
                var look = BodyLook.TryGetValue(x.Label.Text, out var l) ? l : (Color.FromRgb(200, 200, 200), 0);
                string label = x.Label.Text == "Moon" && !double.IsNaN(x.Illumination.Value) ? $"Moon {x.Illumination.Value * 100:0}%" : x.Label.Text;
                return new ChartBody(x.RaHours.Value, x.DecDegrees.Value, label, look.Color, look.Radius);
            }).ToList();
            UiThread.Post(() => { _bodyList = list; Bodies = chart; });
        }
        catch (Exception) { }
    }

    /// <summary>Altitude now, and when the selection rises, culminates and sets, from the site service.</summary>
    private async Task DescribeVisibilityAsync(AtlasHitItem item)
    {
        if (Site is null) { VisibilityText = _siteState is null ? "" : "set your site (Rig › Site) to see when this is up"; return; }
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<ObservabilityRequest, ObservabilityResult>(SiteIds.Observability(SiteIds.Default),
                new ObservabilityRequest { Target = new SkyTarget { RaHours = item.RaHours, DecDegrees = item.DecDegrees, Epoch = "J2000" } }, TimeSpan.FromSeconds(20));
            if (answers?.FirstOrDefault() is not { Ok.Value: true } o) return;
            static string Local(string iso) => iso == "" ? "" : DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("HH:mm");
            var parts = new List<string> { $"now alt {o.Altitude.Value:0.0}°  az {o.Azimuth.Value:0}°" + (o.AboveHorizon.Value ? "" : " (below your horizon)") };
            if (o.RiseUtc.Text != "") parts.Add("rises " + Local(o.RiseUtc.Text));
            if (o.TransitUtc.Text != "") parts.Add($"culminates {Local(o.TransitUtc.Text)} at {o.TransitAltitude.Value:0}°");
            if (o.SetUtc.Text != "") parts.Add("sets " + Local(o.SetUtc.Text));
            if (o.Message.Text != "") parts.Add(o.Message.Text);
            parts.Add($"{o.DarkHoursVisible.Value:0.#} h dark and up in the next 24 h");
            parts.Add($"{o.MoonSeparationDegrees.Value:0}° from the Moon");
            UiThread.Post(() => { if (Selection == item) VisibilityText = string.Join("  ·  ", parts); });
        }
        catch (Exception) { }
    }

    partial void OnCenterRaChanged(double value) => ScheduleRefresh();
    partial void OnCenterDecChanged(double value) => ScheduleRefresh();
    partial void OnFovChanged(double value) => ScheduleRefresh();

    /// <summary>Faintest stars to show for a field of view (about magnitude 5.5 for the whole sky, 13 for half a degree).</summary>
    public static double StarLimitFor(double fov) => Math.Clamp(5.0 + 3.3 * Math.Log10(120 / Math.Max(fov, 0.05)), 5.5, 14);

    private void ScheduleRefresh()
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(120, cts.Token); await RefreshAsync(cts.Token); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Fetch what the current view needs from the atlas.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        double ra = CenterRa, dec = CenterDec, fov = Fov, limit = StarLimitFor(fov);
        var q = new AtlasQuery
        {
            RaHours = ra, DecDegrees = dec, RadiusDegrees = Math.Min(180, fov * 0.8), StarMagnitudeLimit = limit,
            DsoMagnitudeLimit = Math.Clamp(limit + 2.5, 8, 16), MaxStars = 25000, MaxDsos = 3000,
        };
        Interlocked.Increment(ref _queries);
        try
        {
            var chunks = await _mesh.Node.CallFunctionAsync<AtlasQuery, AtlasChunk>(AtlasIds.Query, q, cancellationToken: ct);
            var chunk = chunks?.FirstOrDefault();
            if (chunk is null || ct.IsCancellationRequested) return;
            var stars = chunk.Stars.Select(s => new ChartStar(s.RaHours.value, s.DecDegrees.value, s.Magnitude.value, s.ColorIndex.value, s.Label.Text)).ToList();
            var dsos = chunk.Dsos.Select(d => new ChartDso(d.Id.Text, d.Kind.Text, d.CommonName.Text, d.RaHours.value, d.DecDegrees.value, d.Magnitude.value, d.MajorArcmin.value, d.MinorArcmin.value, d.PositionAngle.value)).ToList();
            UiThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Stars = stars; Dsos = dsos; StarLimit = limit;
                Status = $"{stars.Count} stars to mag {limit:0.#}, {dsos.Count} deep-sky objects" + (chunk.Truncated.Value ? " (brightest only)" : "");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UiThread.Post(() => Status = "atlas: " + ex.Message); }
    }

    // ---- equipment on the chart ----------------------------------------------------------------------------------

    private void RebuildPointers()
    {
        var c = _catalog.Composition;
        var ids = c.MountPointers.Select(p => p.Id.Text).Concat(c.Scopes.Select(s => s.Id.Text)).ToList();
        if (!Pointers.SequenceEqual(ids)) { Pointers.Clear(); foreach (var i in ids) Pointers.Add(i); }
        SelectedPointer ??= Pointers.FirstOrDefault();
    }

    private static readonly Color[] MountColors = { Color.FromRgb(255, 90, 90), Color.FromRgb(255, 170, 60), Color.FromRgb(200, 110, 255), Color.FromRgb(90, 230, 230) };

    /// <summary>Draw every mount where it points (J2000).</summary>
    private async Task FollowEquipmentAsync()
    {
        foreach (var m in _catalog.OfKind(DeviceKinds.Mount).ToList())
        {
            string key = "Mount/" + m.Id;
            if (_followers.ContainsKey(key)) continue;
            var color = MountColors[_followers.Count % MountColors.Length];
            var f = new Follower<MountState>(_mesh.Node, EquipmentIds.State(DeviceKinds.Mount, m.Id), EquipmentIds.GetState(DeviceKinds.Mount, m.Id), s =>
            {
                if (!s.Connected.Value) { _mounts.Remove(key); UpdateOverlays(); return; }
                double ra = s.RaHours.Value, dec = s.DecDegrees.Value;
                if (s.Epoch.Text == "JNow") (ra, dec) = Precession.DateToJ2000(ra, dec, DateTime.UtcNow);
                _mounts[key] = new ChartMarker(ra, dec, $"{m.DisplayName} ({s.Phase.Text})", color);
                UpdateOverlays();
            });
            _followers[key] = f;
            await f.StartAsync();
        }
    }

    private void UpdateOverlays()
    {
        var markers = _mounts.Values.ToList();
        if (Selection is { } sel) markers.Add(new ChartMarker(sel.RaHours, sel.DecDegrees, "", Color.FromRgb(120, 255, 140), IsSelection: true));
        Markers = markers;

    }

    // ---- picking and search ----------------------------------------------------------------------------------------

    /// <summary>From the chart: the nearest star or deep-sky object within a few pixels of the click, or just that position.</summary>
    [RelayCommand]
    private void Pick((double RaHours, double DecDegrees, double PixelsPerDegree) at)
    {
        double tolerance = 10 / Math.Max(at.PixelsPerDegree, 1e-9);
        AtlasHitItem? best = null; double bestSep = double.MaxValue;
        foreach (var d in Dsos)
        {
            double sep = Sky.SeparationDegrees(at.RaHours, at.DecDegrees, d.RaHours, d.DecDegrees);
            double reach = Math.Max(tolerance, d.MajorArcmin / 120);
            if (sep <= reach && sep < bestSep) { bestSep = sep; best = new AtlasHitItem(d.Id, d.Kind, d.CommonName, d.RaHours, d.DecDegrees, d.Magnitude, d.MajorArcmin); }
        }
        foreach (var s in Stars)
        {
            double sep = Sky.SeparationDegrees(at.RaHours, at.DecDegrees, s.RaHours, s.DecDegrees);
            if (sep <= tolerance && sep < bestSep * 0.7) { bestSep = sep; best = new AtlasHitItem(s.Label != "" ? s.Label : "Star", "Star", "", s.RaHours, s.DecDegrees, s.Magnitude, 0); }
        }
        Select(best ?? new AtlasHitItem("Position", "Position", "", at.RaHours, at.DecDegrees, float.NaN, 0));
    }

    public void Select(AtlasHitItem item)
    {
        Selection = item;
        SelectionText = $"{item.Label}  ·  {item.Kind}" + (float.IsNaN(item.Magnitude) ? "" : $"  ·  mag {item.Magnitude:0.##}") +
                        $"\nRA {Sexagesimal.Format(item.RaHours)}   Dec {Sexagesimal.Format(item.DecDegrees, 0)}  (J2000)" +
                        (item.MajorArcmin > 0 ? $"   size {item.MajorArcmin:0.#}'" : "") + (item.Detail != "" ? $"\n{item.Detail}" : "");
        VisibilityText = "";
        _ = DescribeVisibilityAsync(item);
        UpdateOverlays();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Results.Clear();
        if (SearchText.Trim() == "") return;
        // the Sun, Moon and planets come from the site service (they move)
        foreach (var b in _bodyList.Where(b => b.Label.Text.StartsWith(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))
            Results.Add(new AtlasHitItem(b.Label.Text, b.Label.Text is "Sun" or "Moon" ? b.Label.Text : "Planet",
                double.IsNaN(b.Altitude.Value) ? "" : $"alt {b.Altitude.Value:0}°", b.RaHours.Value, b.DecDegrees.Value, float.NaN, b.Label.Text is "Sun" or "Moon" ? 31 : 0));
        var answers = await _mesh.Node.CallFunctionAsync<BinaryConvertibleString, AtlasHits>(AtlasIds.Search, (BinaryConvertibleString)SearchText.Trim());
        foreach (var h in (answers?.FirstOrDefault()?.Hits ?? new()).Where(h => !Results.Any(r => r.Label == h.Label.Text)))
            Results.Add(new AtlasHitItem(h.Label.Text, h.Kind.Text, h.Detail.Text, h.RaHours.Value, h.DecDegrees.Value, h.Magnitude.value, h.MajorArcmin.value));
        Say(Results.Count == 0 ? $"nothing called '{SearchText}'" : "");
        ResultsOpen = Results.Count > 1;
        if (Results.Count > 0) SelectedResult = Results[0];
    }

    partial void OnSelectedResultChanged(AtlasHitItem? value) { if (value is not null) { CenterOn(value); if (Results.Count > 1 && ResultsOpen) ResultsOpen = false; } }

    /// <summary>Centre the chart on an object at a sensible zoom, and select it.</summary>
    public void CenterOn(AtlasHitItem item)
    {
        CenterRa = item.RaHours; CenterDec = item.DecDegrees;
        Fov = item.MajorArcmin > 0 ? Math.Clamp(item.MajorArcmin / 60 * 4, 0.5, 30) : Math.Min(Fov, 15);
        Select(item);
    }

    // ---- acting on the selection -----------------------------------------------------------------------------------

    private SkyTarget? Target() => Selection is { } s ? new SkyTarget { RaHours = s.RaHours, DecDegrees = s.DecDegrees, Epoch = "J2000" } : null;

    private bool CanGoto() => Selection is not null && SelectedPointer is not null;
    private bool HasSelection() => Selection is not null;

    [RelayCommand(CanExecute = nameof(CanGoto))]
    private async Task GotoAsync()
    {
        if (Target() is not { } t) { Say("select something first"); return; }
        if (SelectedPointer is null) { Say("set up a scope first (Rig › Set up)"); return; }
        var r = await Commands.CallAsync(_mesh.Node, PointerIds.Goto(SelectedPointer), t);
        if (r.Ok.Value) Say($"{SelectedPointer}: going to {Selection!.Label}", "ok"); else Say(r.Error.Text);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ImageThis()
    {
        if (Selection is not { } s || _image is null) { Say("select something first"); return; }
        _image.CenterRa = Sexagesimal.Format(s.RaHours, 0); _image.CenterDec = Sexagesimal.Format(s.DecDegrees, 0);
        if (s.Label != "Position" && s.Label != "Star") _image.Label = s.Label.Replace(" ", "");
        // big objects become an area, small ones a single frame
        if (s.MajorArcmin > 30) { _image.Width = Math.Round(s.MajorArcmin / 60 * 1.3, 2); _image.Height = Math.Round(s.MajorArcmin / 60 * 1.0, 2); }
        else { _image.Width = 0; _image.Height = 0; }
        Say($"Framing {s.Label}: drag the frame on the chart to adjust it", "info");
    }

    [RelayCommand]
    private void CenterOnMount()
    {
        var m = _mounts.Values.FirstOrDefault();
        if (m is null) { Say("no connected mount"); return; }
        CenterRa = m.RaHours; CenterDec = m.DecDegrees;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ShowInStellariumAsync()
    {
        if (Target() is not { } t) { Say("select something first"); return; }
        var r = await Commands.CallAsync(_mesh.Node, StellariumIds.Show, t);
        if (r.Ok.Value) Say("shown in Stellarium", "ok"); else Say(r.Error.Text);
    }

    [RelayCommand]
    private async Task TakeStellariumSelectionAsync()
    {
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, AtlasHit>(StellariumIds.GetSelection, NOTESVoid.Void);
        var h = answers?.FirstOrDefault();
        if (h is null || h.Label.Text == "") { Say("nothing is selected in Stellarium (or it is not reachable)"); return; }
        CenterOn(new AtlasHitItem(h.Label.Text, h.Kind.Text, "from Stellarium", h.RaHours.Value, h.DecDegrees.Value, h.Magnitude.value, 0));
        Say("");
    }

    [RelayCommand]
    private async Task StellariumFollowsPointerAsync()
    {
        if (SelectedPointer is null) { Say("set up a scope first (Rig › Set up)"); return; }
        var r = await Commands.CallAsync(_mesh.Node, StellariumIds.BindPointer, (BinaryConvertibleString)SelectedPointer);
        if (r.Ok.Value) Say($"Stellarium now shows and slews {SelectedPointer}", "ok"); else Say(r.Error.Text);
    }

    [RelayCommand] private void ZoomIn() => Fov = Math.Max(0.1, Fov / 1.6);
    [RelayCommand] private void ZoomOut() => Fov = Math.Min(180, Fov * 1.6);

    public void Dispose()
    {
        _pending?.Cancel();
        foreach (var f in _followers.Values) f.Dispose();
        _stellarium?.Dispose(); _site?.Dispose(); _skyTimer?.Dispose();
        if (_stellariumSelected is not null) { try { _mesh.Node.UnhookEvent(StellariumIds.Selected, _stellariumSelected); } catch (ObjectDisposedException) { } }
    }
}
