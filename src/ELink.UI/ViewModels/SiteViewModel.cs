using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Controls;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>Where and when: the site's location (typed in or from a GPS), its horizon, and tonight's sky.</summary>
public sealed partial class SiteViewModel : ObservableObject, IDisposable
{
    public const string NoGps = "None: use the numbers";
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<SiteState>? _follower;
    private Timer? _timer;
    private bool _loaded;

    /// <summary>The atlas knows the horizon, the bodies and the paths: the flat horizon is drawn from it.</summary>
    public AtlasViewModel Atlas { get; }

    public SiteViewModel(MeshSession mesh, CatalogViewModel catalog, AtlasViewModel atlas)
    {
        _mesh = mesh; _catalog = catalog; Atlas = atlas;
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildGps);
        RebuildGps();
    }

    public ObservableCollection<string> GpsDevices { get; } = new();
    public ObservableCollection<string> Bodies { get; } = new();
    [ObservableProperty] private string _label = "Home";
    [ObservableProperty] private string _latitude = "";
    [ObservableProperty] private string _longitude = "";
    [ObservableProperty] private double _elevation;
    [ObservableProperty] private string _gps = NoGps;
    [ObservableProperty] private double _minAltitude = 15;
    [ObservableProperty] private string _horizonText = "";
    [ObservableProperty] private bool _pushToMounts = true;

    [ObservableProperty] private bool _known;
    [ObservableProperty] private string _where = "";
    [ObservableProperty] private string _skyText = "";
    [ObservableProperty] private string _nightText = "";
    [ObservableProperty] private string _moonText = "";
    [ObservableProperty] private string _stateMessage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MessageKind))] private string _message = "";
    public string MessageKind => Message == "saved" ? "ok" : "error";
    /// <summary>The next 24 hours: dark hours and the Moon's, for the bar on the page.</summary>
    [ObservableProperty] private NightTimeline? _night;

    private void RebuildGps()
    {
        var list = new[] { NoGps }.Concat(_catalog.OfKind(DeviceKinds.Gps).Select(d => d.Id)).ToList();
        if (GpsDevices.SequenceEqual(list)) return;
        GpsDevices.Clear(); foreach (var g in list) GpsDevices.Add(g);
    }

    private static string Local(string iso) => iso == "" ? "—" :
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("ddd HH:mm");

    public async Task StartAsync()
    {
        _follower = new Follower<SiteState>(_mesh.Node, SiteIds.State(SiteIds.Default), SiteIds.GetState(SiteIds.Default), s =>
        {
            var c = s.Config;
            if (!_loaded && (!double.IsNaN(c.LatitudeDegrees.Value) || c.GpsId.Text != ""))
            {
                _loaded = true;
                Label = c.Label.Text;
                Latitude = double.IsNaN(c.LatitudeDegrees.Value) ? "" : Sexagesimal.Format(c.LatitudeDegrees.Value, 0);
                Longitude = double.IsNaN(c.LongitudeDegrees.Value) ? "" : Sexagesimal.Format(c.LongitudeDegrees.Value, 0);
                Elevation = c.ElevationMeters.Value; Gps = c.GpsId.Text == "" ? NoGps : c.GpsId.Text;
                MinAltitude = c.MinAltitudeDegrees.Value; PushToMounts = c.PushToMounts.Value;
                HorizonText = string.Join(", ", c.Horizon.Select(p => FormattableString.Invariant($"{p.AzimuthDegrees.Value:0.#}:{p.AltitudeDegrees.Value:0.#}")));
            }
            Known = s.Known.Value;
            StateMessage = s.Message.Text;
            if (!s.Known.Value) { Where = "site unknown"; SkyText = NightText = MoonText = ""; Night = null; return; }
            Night = Timeline(s);
            Where = $"{s.Config.Label.Text}: {Sexagesimal.Format(s.Config.LatitudeDegrees.Value, 0)}  {Sexagesimal.Format(s.Config.LongitudeDegrees.Value, 0)}  {s.Config.ElevationMeters.Value:0} m   ·   from {s.Source.Text}" +
                    (double.IsNaN(s.ClockOffsetSeconds.Value) ? "" : $"   ·   clock {s.ClockOffsetSeconds.Value:+0.0;-0.0} s vs GPS");
            SkyText = $"{Spaced(s.Sky.Text)}   ·   Sun at {s.SunAltitude.Value:0.0}°   ·   local sidereal time {Sexagesimal.Format(s.LocalSiderealHours.Value, 0)}";
            NightText = $"astronomical dusk {Local(s.DuskUtc.Text)}   ·   dawn {Local(s.DawnUtc.Text)}";
            MoonText = $"Moon {s.MoonIllumination.Value * 100:0}% lit, at {s.MoonAltitude.Value:0}°   ·   rises {Local(s.MoonRiseUtc.Text)}   ·   sets {Local(s.MoonSetUtc.Text)}";
            _ = RefreshBodiesAsync();
        });
        await _follower.StartAsync();
        _timer = new Timer(_ => _ = RefreshBodiesAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static DateTime? Parse(string iso) => iso == "" ? null : DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime();

    /// <summary>When it is dark and when the Moon is up over the next 24 hours, from what the site service says of the next events.</summary>
    public static NightTimeline Timeline(SiteState s, DateTime? now = null)
    {
        var at = now ?? DateTime.Now; var from = at.AddHours(-2); var to = at.AddHours(22);
        var dark = new List<(DateTime, DateTime)>();
        var dusk = Parse(s.DuskUtc.Text); var dawn = Parse(s.DawnUtc.Text);
        if (s.Sky.Text == "Night")
        {
            if (dawn is { } d) { dark.Add((from, d)); if (dusk is { } u && u > d) dark.Add((u, to)); } else dark.Add((from, to));
        }
        else if (dusk is { } u2) dark.Add((u2, dawn is { } d2 && d2 > u2 ? d2 : to));
        var moon = new List<(DateTime, DateTime)>();
        var rise = Parse(s.MoonRiseUtc.Text); var set = Parse(s.MoonSetUtc.Text);
        if (s.MoonAltitude.Value > 0)
        {
            var end = set ?? to; moon.Add((from, end));
            if (rise is { } r && r > end) moon.Add((r, to));
        }
        else if (rise is { } r2) moon.Add((r2, set is { } s2 && s2 > r2 ? s2 : to));
        static (DateTime, DateTime) Clip((DateTime A, DateTime B) x, DateTime lo, DateTime hi) => (x.A < lo ? lo : x.A, x.B > hi ? hi : x.B);
        return new NightTimeline(at, from, to, dark.Select(x => Clip(x, from, to)).Where(x => x.Item2 > x.Item1).ToList(), moon.Select(x => Clip(x, from, to)).Where(x => x.Item2 > x.Item1).ToList());
    }

    private static string Spaced(string phase) => string.Concat(phase.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private async Task RefreshBodiesAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, SkyBodies>(SiteIds.Bodies(SiteIds.Default), NOTESVoid.Void, TimeSpan.FromSeconds(10));
            if (answers?.FirstOrDefault() is not { } b) return;
            var lines = b.Bodies.Select(x => $"{x.Label.Text,-8} " + (double.IsNaN(x.Altitude.Value) ? "" : $"alt {x.Altitude.Value,6:0.0}°  az {x.Azimuth.Value,4:0}°   ") +
                                             $"RA {Sexagesimal.Format(x.RaHours.Value, 0)}  Dec {Sexagesimal.Format(x.DecDegrees.Value, 0)}" +
                                             (double.IsNaN(x.Illumination.Value) ? "" : $"   {x.Illumination.Value * 100:0}% lit") +
                                             (x.Label.Text == "Sun" ? "" : $"   {x.ElongationDegrees.Value:0}° from the Sun")).ToList();
            UiThread.Post(() => { Bodies.Clear(); foreach (var l in lines) Bodies.Add(l); });
        }
        catch (Exception) { }
    }

    /// <summary>"az:alt, az:alt" to horizon points.</summary>
    public static bool TryParseHorizon(string text, out List<(double Az, double Alt)> points)
    {
        points = new();
        foreach (var part in text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(':', StringSplitOptions.TrimEntries);
            if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var az) ||
                !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt)) return false;
            points.Add((az, alt));
        }
        return true;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        double lat = double.NaN, lon = double.NaN;
        if (Latitude.Trim() != "" && !Sexagesimal.TryParse(Latitude, out lat)) { Message = "latitude: degrees, e.g. 47.5 or 47:29:52"; return; }
        if (Longitude.Trim() != "" && !Sexagesimal.TryParse(Longitude, out lon)) { Message = "longitude: degrees east, e.g. 19.04 or -70:24:15"; return; }
        if (!TryParseHorizon(HorizonText, out var horizon)) { Message = "horizon: azimuth:altitude pairs, e.g. 180:10, 200:35, 260:35, 280:10"; return; }
        var c = new SiteConfig
        {
            Label = Label.Trim() == "" ? "Home" : Label.Trim(), LatitudeDegrees = lat, LongitudeDegrees = lon, ElevationMeters = Elevation,
            GpsId = Gps == NoGps ? "" : Gps, PushToMounts = PushToMounts, MinAltitudeDegrees = MinAltitude,
        };
        foreach (var (az, alt) in horizon) c.Horizon.Add(new HorizonPoint { AzimuthDegrees = az, AltitudeDegrees = alt });
        var r = await Commands.CallAsync(_mesh.Node, SiteIds.Configure(SiteIds.Default), c);
        _loaded = true;
        Message = r.Ok.Value ? "saved" : r.Error.Text;
    }

    public void Dispose() { _follower?.Dispose(); _timer?.Dispose(); }
}
