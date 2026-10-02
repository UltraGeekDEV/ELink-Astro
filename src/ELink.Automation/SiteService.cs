using System.Globalization;
using System.Text.Json;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Where and when: the observing site (typed in, or from a GPS), sidereal time, twilight, the Moon and planets,
/// and when any target is up. A shared fact scopes may consult; it never drives them. Optionally hands the location and
/// time to every mount on the mesh, as Ekos does when a mount connects.</summary>
public sealed class SiteService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _id;
    private readonly string? _path;
    private readonly CommandSet _commands;
    private readonly StatePublisher<SiteState> _publisher;
    private readonly object _gate = new();
    private readonly HashSet<string> _pushed = new();
    private SiteConfig _config = new();
    private RemoteState<GpsState>? _gps;
    private GeoSite? _gpsFix;
    private double _clockOffset = double.NaN;
    private string _message = "";
    private Timer? _timer;
    private Action<DeviceAnnouncement>? _announce;

    /// <summary>The clock, replaceable in tests.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(30);

    public SiteService(TypeSafeEVentNode node, string? path = null, string id = SiteIds.Default)
    {
        _node = node; _id = id; _path = path;
        _commands = new CommandSet(node);
        _publisher = new(node, SiteIds.State(id), SiteIds.GetState(id), BuildState);
        if (path is not null && File.Exists(path))
        {
            try { _config = Load(File.ReadAllText(path)); }
            catch (Exception ex) { _message = $"could not read {path}: {ex.Message}"; }
        }
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<SiteConfig, CommandResult>(SiteIds.Configure(_id), ConfigureAsync, "set the site: location (or a GPS), horizon, push to mounts");
        await _commands.AddAsync<ObservabilityRequest, ObservabilityResult>(SiteIds.Observability(_id), r => Task.FromResult(Observability(r)), "when a target is up, transits, and how much dark time it gets");
        await _commands.AddAsync<NOTESVoid, SkyBodies>(SiteIds.Bodies(_id), _ => Task.FromResult(Bodies()), "Sun, Moon and planets now");
        await _publisher.StartAsync();
        _announce = a => { if (a.Present.Value && a.Device.Kind.Text == DeviceKinds.Mount) _ = PushAsync(); };
        await _node.HookEventAsync(EquipmentIds.Announce, _announce, "site: new mounts");
        await FollowGpsAsync();
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.Zero, Tick);
    }

    private async Task TickAsync()
    {
        try { await PushAsync(); await _publisher.PublishAsync(); } catch (ObjectDisposedException) { }
    }

    // ---- configuration ---------------------------------------------------------------------------------------

    private async Task<CommandResult> ConfigureAsync(SiteConfig c)
    {
        bool manual = c.GpsId.Text == "";
        if (manual && (double.IsNaN(c.LatitudeDegrees.Value) || double.IsNaN(c.LongitudeDegrees.Value)))
            return CommandResult.Fail("give a latitude and longitude, or a GPS");
        if (!double.IsNaN(c.LatitudeDegrees.Value) && Math.Abs(c.LatitudeDegrees.Value) > 90) return CommandResult.Fail("latitude is -90..90");
        if (!double.IsNaN(c.LongitudeDegrees.Value) && Math.Abs(c.LongitudeDegrees.Value) > 180) return CommandResult.Fail("longitude is -180..180 (east positive)");
        if (c.Horizon.Any(p => p.AltitudeDegrees.Value is < -10 or > 90)) return CommandResult.Fail("horizon altitudes are -10..90");
        lock (_gate) { _config = c; _pushed.Clear(); _message = ""; }
        if (_path is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); File.WriteAllText(_path, Save(c)); }
            catch (Exception ex) { lock (_gate) _message = $"not saved: {ex.Message}"; }
        }
        await FollowGpsAsync();
        await PushAsync();
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task FollowGpsAsync()
    {
        string gpsId; lock (_gate) gpsId = _config.GpsId.Text;
        if (_gps is not null) { _gps.Dispose(); _gps = null; lock (_gate) { _gpsFix = null; _clockOffset = double.NaN; } }
        if (gpsId == "") return;
        _gps = new RemoteState<GpsState>(_node, EquipmentIds.State(DeviceKinds.Gps, gpsId), EquipmentIds.GetState(DeviceKinds.Gps, gpsId));
        _gps.Changed += OnGps;
        await _gps.StartAsync();
        if (_gps.Latest is { } g) OnGps(g);
    }

    private void OnGps(GpsState g)
    {
        if (!g.HasFix.Value || double.IsNaN(g.LatitudeDegrees.Value) || double.IsNaN(g.LongitudeDegrees.Value)) return;
        double lon = g.LongitudeDegrees.Value > 180 ? g.LongitudeDegrees.Value - 360 : g.LongitudeDegrees.Value;
        var fix = new GeoSite(g.LatitudeDegrees.Value, lon, double.IsNaN(g.ElevationMeters.Value) ? 0 : g.ElevationMeters.Value);
        bool moved;
        lock (_gate)
        {
            moved = _gpsFix is not { } old || Sky.SeparationDegrees(0, old.LatitudeDegrees, (old.LongitudeDegrees - fix.LongitudeDegrees) / 15, fix.LatitudeDegrees) > 0.001;
            _gpsFix = fix;
            if (DateTime.TryParse(g.TimeUtc.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                _clockOffset = (t - UtcNow()).TotalSeconds;
            if (moved) _pushed.Clear();
        }
        if (moved) _ = TickAsync();
    }

    /// <summary>The site in use: the GPS fix when a GPS is configured and has one, else the typed-in numbers.</summary>
    public GeoSite? Site
    {
        get
        {
            lock (_gate)
            {
                if (_config.GpsId.Text != "") return _gpsFix ?? Manual();
                return Manual();
            }
            GeoSite? Manual() => double.IsNaN(_config.LatitudeDegrees.Value) || double.IsNaN(_config.LongitudeDegrees.Value)
                ? null : new GeoSite(_config.LatitudeDegrees.Value, _config.LongitudeDegrees.Value, _config.ElevationMeters.Value);
        }
    }

    /// <summary>Lowest altitude that counts as above the horizon at an azimuth: the flat minimum or the profile, whichever is higher.</summary>
    public double MinAltitude(double azimuth)
    {
        SiteConfig c; lock (_gate) c = _config;
        return Horizon.ProfileAltitude(c.MinAltitudeDegrees.Value, c.Horizon.Select(p => (p.AzimuthDegrees.Value, p.AltitudeDegrees.Value)).ToList(), azimuth);
    }

    // ---- mounts -----------------------------------------------------------------------------------------------

    private async Task PushAsync()
    {
        GeoSite? site = Site;
        bool push; lock (_gate) push = _config.PushToMounts.Value;
        if (!push || site is not { } s) return;
        List<string> mounts;
        try
        {
            var lists = await _node.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(5));
            mounts = (lists ?? []).SelectMany(l => l.Devices).Where(d => d.Kind.Text == DeviceKinds.Mount).Select(d => d.Id.Text).Distinct().ToList();
        }
        catch (Exception) { return; }
        foreach (var m in mounts)
        {
            lock (_gate) if (_pushed.Contains(m)) continue;
            var loc = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, m, SiteIds.MountSetLocation),
                new GeoLocation { LatitudeDegrees = s.LatitudeDegrees, LongitudeDegrees = s.LongitudeDegrees, ElevationMeters = s.ElevationMeters });
            if (!loc.Ok.Value) continue;   // not connected yet, or a mount that keeps its own: try again next tick
            await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, m, SiteIds.MountSetTime), (BinaryConvertibleString)"");
            lock (_gate) _pushed.Add(m);
        }
    }

    // ---- sky -----------------------------------------------------------------------------------------------------

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static double SunAlt(DateTime t, GeoSite s)
    {
        var p = SolarSystem.Position(Body.Sun, t);
        return Horizon.AltAzJ2000(p.RaHours, p.DecDegrees, t, s).Altitude;
    }

    private static (double Ra, double Dec) Moon(DateTime t, GeoSite s) => SolarSystem.Topocentric(SolarSystem.Position(Body.Moon, t), t, s);

    public static string SkyPhase(double sunAltitude) => sunAltitude switch
    {
        >= -0.833 => "Day", >= -6 => "CivilTwilight", >= -12 => "NauticalTwilight", >= -18 => "AstronomicalTwilight", _ => "Night",
    };

    private SiteState BuildState()
    {
        var now = UtcNow();
        SiteConfig c; string message; double offset; bool gps; bool fix;
        lock (_gate) { c = _config; message = _message; offset = _clockOffset; gps = c.GpsId.Text != ""; fix = _gpsFix is not null; }
        var st = new SiteState { Config = c, UtcNow = Iso(now), ClockOffsetSeconds = offset, Message = message };
        if (Site is not { } s)
        {
            st.Source = "(none)";
            st.Message = message != "" ? message : gps ? "waiting for a GPS fix" : "set the site's latitude and longitude";
            return st;
        }
        st.Known = true;
        st.Source = gps && fix ? "GPS " + c.GpsId.Text : "Manual";
        if (gps && !fix && message == "") st.Message = "no GPS fix yet: using the typed-in location";
        if (!double.IsNaN(offset) && Math.Abs(offset) > 2 && st.Message.Text == "") st.Message = $"this computer's clock is {offset:+0.0;-0.0} s off GPS time";
        st.LocalSiderealHours = Horizon.LocalSiderealHours(now, s.LongitudeDegrees);
        double sun = SunAlt(now, s);
        st.SunAltitude = sun; st.Sky = SkyPhase(sun);
        var sunEvents = Horizon.Events(t => SunAlt(t, s), -18, now, now.AddHours(36), TimeSpan.FromMinutes(15));
        if (sunEvents.FirstOrDefault(e => e.Kind == Horizon.EventKind.Set) is { Utc.Year: > 1 } dusk) st.DuskUtc = Iso(dusk.Utc);
        if (sunEvents.FirstOrDefault(e => e.Kind == Horizon.EventKind.Rise) is { Utc.Year: > 1 } dawn) st.DawnUtc = Iso(dawn.Utc);
        var moonNow = SolarSystem.Position(Body.Moon, now);
        var (mra, mdec) = SolarSystem.Topocentric(moonNow, now, s);
        st.MoonRaHours = mra; st.MoonDecDegrees = mdec; st.MoonIllumination = moonNow.Illumination;
        st.MoonAltitude = Horizon.AltAzJ2000(mra, mdec, now, s).Altitude;
        double MoonAlt(DateTime t) { var (r, d) = Moon(t, s); return Horizon.AltAzJ2000(r, d, t, s).Altitude; }
        var moonEvents = Horizon.Events(MoonAlt, 0.125 - 0.583, now, now.AddHours(30), TimeSpan.FromMinutes(15));   // upper limb with refraction
        if (moonEvents.FirstOrDefault(e => e.Kind == Horizon.EventKind.Rise) is { Utc.Year: > 1 } mr) st.MoonRiseUtc = Iso(mr.Utc);
        if (moonEvents.FirstOrDefault(e => e.Kind == Horizon.EventKind.Set) is { Utc.Year: > 1 } ms) st.MoonSetUtc = Iso(ms.Utc);
        return st;
    }

    public ObservabilityResult Observability(ObservabilityRequest r)
    {
        var res = new ObservabilityResult();
        if (Site is not { } s) { res.Message = "the site is not known yet"; return res; }
        DateTime from = UtcNow();
        if (r.FromUtc.Text.Trim() != "" && !DateTime.TryParse(r.FromUtc.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out from))
        { res.Message = "FromUtc must be ISO 8601"; return res; }
        double ra = r.Target.RaHours.Value, dec = r.Target.DecDegrees.Value;
        if (double.IsNaN(ra) || double.IsNaN(dec)) { res.Message = "no target"; return res; }
        if (r.Target.Epoch.Text == "JNow") (ra, dec) = Precession.DateToJ2000(ra, dec, from);
        double fixedMin = r.MinAltitudeDegrees.Value;
        double Margin(DateTime t)
        {
            var (alt, az) = Horizon.AltAzJ2000(ra, dec, t, s);
            return alt - (double.IsNaN(fixedMin) ? MinAltitude(az) : fixedMin);
        }
        double Alt(DateTime t) => Horizon.AltAzJ2000(ra, dec, t, s).Altitude;
        var to = from.AddHours(Math.Clamp(r.Hours.Value, 0.1, 240));

        var (a0, z0) = Horizon.AltAzJ2000(ra, dec, from, s);
        var (rad, decd) = Precession.J2000ToDate(ra, dec, from);
        res.Altitude = a0; res.Azimuth = z0; res.HourAngleHours = Horizon.HourAngleHours(rad, from, s.LongitudeDegrees);
        res.AboveHorizon = Margin(from) > 0;
        var crossings = Horizon.Events(Margin, 0, from, to).Where(e => e.Kind != Horizon.EventKind.Transit).ToList();
        if (crossings.FirstOrDefault(e => e.Kind == Horizon.EventKind.Rise) is { Utc.Year: > 1 } rise) res.RiseUtc = Iso(rise.Utc);
        if (crossings.FirstOrDefault(e => e.Kind == Horizon.EventKind.Set) is { Utc.Year: > 1 } set) res.SetUtc = Iso(set.Utc);
        var transit = Horizon.Events(Alt, -1000, from, from.AddHours(Math.Max(24.1, r.Hours.Value))).FirstOrDefault(e => e.Kind == Horizon.EventKind.Transit);
        if (transit.Utc.Year > 1) { res.TransitUtc = Iso(transit.Utc); res.TransitAltitude = transit.Altitude; }
        double dark = 0;
        for (var t = from; t < to; t = t.AddMinutes(5))
            if (Margin(t) > 0 && SunAlt(t, s) < -18) dark += 5 / 60.0;
        res.DarkHoursVisible = Math.Round(dark, 2);
        var (mra, mdec) = Moon(from, s);
        res.MoonSeparationDegrees = Sky.SeparationDegrees(ra, dec, mra, mdec);
        res.Ok = true;
        if (crossings.Count == 0 && !res.AboveHorizon.Value) res.Message = "does not rise above the horizon in this window";
        else if (crossings.Count == 0) res.Message = "stays above the horizon all the time";
        return res;
    }

    public SkyBodies Bodies()
    {
        var now = UtcNow();
        var site = Site;
        var list = new SkyBodies { UtcAt = Iso(now) };
        foreach (var b in Enum.GetValues<Body>())
        {
            var p = SolarSystem.Position(b, now);
            var (ra, dec) = site is { } s ? SolarSystem.Topocentric(p, now, s) : (p.RaHours, p.DecDegrees);
            var body = new SkyBody { Label = b.ToString(), RaHours = ra, DecDegrees = dec, DistanceAu = p.DistanceAu, Illumination = p.Illumination, ElongationDegrees = p.ElongationDegrees };
            if (site is { } s2) { var (alt, az) = Horizon.AltAzJ2000(ra, dec, now, s2); body.Altitude = alt; body.Azimuth = az; }
            list.Bodies.Add(body);
        }
        return list;
    }

    // ---- persistence -------------------------------------------------------------------------------------------

    private sealed record Stored(string Label, double? Latitude, double? Longitude, double Elevation, string GpsId, bool PushToMounts, double MinAltitude, List<double[]> Horizon);

    private static string Save(SiteConfig c) => JsonSerializer.Serialize(new Stored(c.Label.Text,
        double.IsNaN(c.LatitudeDegrees.Value) ? null : c.LatitudeDegrees.Value, double.IsNaN(c.LongitudeDegrees.Value) ? null : c.LongitudeDegrees.Value,
        c.ElevationMeters.Value, c.GpsId.Text, c.PushToMounts.Value, c.MinAltitudeDegrees.Value,
        c.Horizon.Select(p => new[] { p.AzimuthDegrees.Value, p.AltitudeDegrees.Value }).ToList()), new JsonSerializerOptions { WriteIndented = true });

    private static SiteConfig Load(string json)
    {
        var s = JsonSerializer.Deserialize<Stored>(json) ?? throw new FormatException("empty");
        var c = new SiteConfig
        {
            Label = s.Label ?? "Home", LatitudeDegrees = s.Latitude ?? double.NaN, LongitudeDegrees = s.Longitude ?? double.NaN, ElevationMeters = s.Elevation,
            GpsId = s.GpsId ?? "", PushToMounts = s.PushToMounts, MinAltitudeDegrees = s.MinAltitude,
        };
        foreach (var p in s.Horizon ?? []) if (p.Length == 2) c.Horizon.Add(new HorizonPoint { AzimuthDegrees = p[0], AltitudeDegrees = p[1] });
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync();
        if (_announce is not null) { try { _node.UnhookEvent(EquipmentIds.Announce, _announce); } catch (ObjectDisposedException) { } }
        _gps?.Dispose(); _commands.Dispose(); _publisher.Dispose();
    }
}
