using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
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

    public SiteViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
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
    [ObservableProperty] private string _message = "";

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
            if (!s.Known.Value) { Where = "site unknown"; SkyText = NightText = MoonText = ""; return; }
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
