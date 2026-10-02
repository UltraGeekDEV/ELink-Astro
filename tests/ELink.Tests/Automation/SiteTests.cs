using ELink.Automation;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Indi.Client;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

public class SiteTests : IAsyncLifetime
{
    private static readonly DateTime Winter = new(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);
    private TypeSafeEVentNode _node = null!;
    private SiteService _site = null!;
    private string _dir = null!;
    private SiteState? _last;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "elink-site-" + Guid.NewGuid().ToString("N"));
        _node = ElinkNode.Create("ST-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _site = new SiteService(_node, Path.Combine(_dir, "site.json")) { UtcNow = () => Winter, Tick = TimeSpan.FromMilliseconds(300) };
        await _site.StartAsync();
        await _node.HookEventAsync(SiteIds.State(SiteIds.Default), (SiteState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _site.DisposeAsync(); _node.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static SiteConfig Budapest(params (double Az, double Alt)[] horizon)
    {
        var c = new SiteConfig { Label = "Budapest", LatitudeDegrees = 47.4979, LongitudeDegrees = 19.0402, ElevationMeters = 100, MinAltitudeDegrees = 10 };
        foreach (var (az, alt) in horizon) c.Horizon.Add(new HorizonPoint { AzimuthDegrees = az, AltitudeDegrees = alt });
        return c;
    }

    private async Task<ObservabilityResult> Observe(double raH, double dec, double minAlt = double.NaN) =>
        Assert.Single((await _node.CallFunctionAsync<ObservabilityRequest, ObservabilityResult>(SiteIds.Observability(SiteIds.Default),
            new ObservabilityRequest { Target = new SkyTarget { RaHours = raH, DecDegrees = dec, Epoch = "J2000" }, MinAltitudeDegrees = minAlt }, TimeSpan.FromSeconds(30)))!);

    [Fact]
    public async Task UnknownUntilConfiguredThenTellsTheSky()
    {
        var first = Assert.Single((await _node.CallFunctionAsync<NOTESVoid, SiteState>(SiteIds.GetState(SiteIds.Default), NOTESVoid.Void))!);
        Assert.False(first.Known.Value);
        Assert.Contains("latitude", first.Message.Text);
        Assert.False((await Observe(5.5, -5)).Ok.Value);
        Assert.False((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), new SiteConfig())).Ok.Value);
        Assert.False((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), new SiteConfig { LatitudeDegrees = 95, LongitudeDegrees = 0 })).Ok.Value);

        Assert.True((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), Budapest())).Ok.Value);
        Assert.True(await Eventually(() => _last is { Known.Value: true }));
        var s = _last!;
        Assert.Equal("Manual", s.Source.Text);
        // 20:00 UTC in mid January in Budapest: dark
        Assert.Equal("Night", s.Sky.Text);
        Assert.True(s.SunAltitude.Value < -18);
        Assert.Equal(Horizon.LocalSiderealHours(Winter, 19.0402), s.LocalSiderealHours.Value, 6);
        var dawn = DateTime.Parse(s.DawnUtc.Text).ToUniversalTime();
        Assert.InRange(dawn, new DateTime(2026, 1, 16, 4, 30, 0, DateTimeKind.Utc), new DateTime(2026, 1, 16, 5, 15, 0, DateTimeKind.Utc));   // sunrise ~06:25 UTC, astro dawn ~1h45 earlier
        Assert.InRange(s.MoonIllumination.Value, 0, 1);

        var bodies = Assert.Single((await _node.CallFunctionAsync<NOTESVoid, SkyBodies>(SiteIds.Bodies(SiteIds.Default), NOTESVoid.Void))!);
        Assert.Equal(9, bodies.Bodies.Count);
        Assert.Equal(s.SunAltitude.Value, bodies.Bodies.First(b => b.Label.Text == "Sun").Altitude.Value, 2);   // bodies are topocentric: 9" of parallax
        Assert.Equal(s.MoonAltitude.Value, bodies.Bodies.First(b => b.Label.Text == "Moon").Altitude.Value, 2);

        // the configuration survives a restart
        await using var again = new SiteService(_node, Path.Combine(_dir, "site.json"), "again") { UtcNow = () => Winter };
        Assert.Equal(new GeoSite(47.4979, 19.0402, 100), again.Site);
    }

    [Fact]
    public async Task ObservabilityOfTargets()
    {
        Assert.True((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), Budapest())).Ok.Value);
        // M42 on a January evening: up, rising toward the meridian, transits around 21:00 local sidereal 5.6h
        var m42 = await Observe(5.588, -5.39);
        Assert.True(m42.Ok.Value, m42.Message.Text);
        Assert.True(m42.AboveHorizon.Value);
        Assert.InRange(m42.HourAngleHours.Value, -2, 0);
        var transit = DateTime.Parse(m42.TransitUtc.Text).ToUniversalTime();
        Assert.Equal(90 - 47.4979 - 5.39, m42.TransitAltitude.Value, 0);
        var (rad, _) = Precession.J2000ToDate(5.588, -5.39, transit);
        Assert.InRange(Horizon.HourAngleHours(rad, transit, 19.0402) * 60, -1, 1);   // within a minute of the meridian
        Assert.NotEqual("", m42.SetUtc.Text);
        Assert.InRange(m42.DarkHoursVisible.Value, 2, 8);

        // the horizon profile: a 40 degree wall in the south-west makes it set earlier
        Assert.True((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), Budapest((180, 10), (200, 40), (260, 40), (280, 10)))).Ok.Value);
        Assert.Equal(40, _site.MinAltitude(230), 6);
        Assert.Equal(25, _site.MinAltitude(190), 6);
        Assert.Equal(10, _site.MinAltitude(90), 6);
        var walled = await Observe(5.588, -5.39);
        Assert.True(DateTime.Parse(walled.SetUtc.Text) < DateTime.Parse(m42.SetUtc.Text));

        var polaris = await Observe(2.53, 89.26);
        Assert.True(polaris.AboveHorizon.Value);
        Assert.Equal("", polaris.RiseUtc.Text); Assert.Equal("", polaris.SetUtc.Text);
        Assert.True(polaris.Message.Text.Contains("stays above"), $"polaris: {polaris.Message.Text} rise {polaris.RiseUtc.Text} set {polaris.SetUtc.Text}");
        var south = await Observe(6, -70);
        Assert.False(south.AboveHorizon.Value);
        Assert.True(south.Message.Text.Contains("does not rise"), $"south: {south.Message.Text} rise {south.RiseUtc.Text} set {south.SetUtc.Text} alt {south.Altitude.Value}");
    }

    [Fact]
    public async Task TakesTheLocationAndClockFromAGps()
    {
        var gps = new GpsState { Connected = true, HasFix = false };
        using var cmds = new CommandSet(_node);
        using var pub = new StatePublisher<GpsState>(_node, EquipmentIds.State(DeviceKinds.Gps, "gps1"), EquipmentIds.GetState(DeviceKinds.Gps, "gps1"), () => gps);
        await pub.StartAsync();
        var cfg = new SiteConfig { GpsId = "gps1" };
        Assert.True((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), cfg)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Known.Value: false } && _last.Message.Text.Contains("GPS")));
        gps = new GpsState { Connected = true, HasFix = true, LatitudeDegrees = -31.27, LongitudeDegrees = 360 - 70.0, ElevationMeters = 2400, TimeUtc = Winter.AddSeconds(5).ToString("yyyy-MM-ddTHH:mm:ss") };
        await pub.PublishAsync();
        Assert.True(await Eventually(() => _last is { Known.Value: true }), _last?.Message.Text);
        Assert.Equal("GPS gps1", _last!.Source.Text);
        Assert.Equal(new GeoSite(-31.27, -70.0, 2400), _site.Site);   // INDI's 0..360 longitude turned into -180..180
        Assert.Equal(5, _last.ClockOffsetSeconds.Value, 1);
        Assert.Contains("clock", _last.Message.Text);
    }
}

/// <summary>The site hands its location and time to a real (simulated) INDI mount.</summary>
public class SiteMountTests : IAsyncLifetime
{
    private IndiServerProcess _indi = null!;
    public async Task InitializeAsync()
    {
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task PushesLocationAndTimeToMounts()
    {
        using var node = ElinkNode.Create("SM-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        await using var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port);
        await link.StartAsync();
        await using var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        await c.WaitForAsync("Telescope Simulator", "CONNECTION", _ => true, TimeSpan.FromSeconds(30));
        await c.SetSwitchAsync("Telescope Simulator", "CONNECTION", "CONNECT");
        await c.WaitForAsync("Telescope Simulator", "GEOGRAPHIC_COORD", _ => true, TimeSpan.FromSeconds(30));

        await using var site = new SiteService(node) { Tick = TimeSpan.FromMilliseconds(500) };
        await site.StartAsync();
        Assert.True((await Commands.CallAsync(node, SiteIds.Configure(SiteIds.Default),
            new SiteConfig { LatitudeDegrees = -24.6272, LongitudeDegrees = -70.4042, ElevationMeters = 2635 })).Ok.Value);
        var geo = await c.WaitForAsync("Telescope Simulator", "GEOGRAPHIC_COORD",
            p => Math.Abs(p.Number("LAT") + 24.6272) < 1e-3, TimeSpan.FromSeconds(30));
        Assert.Equal(360 - 70.4042, geo.Number("LONG"), 3);
        Assert.Equal(2635, geo.Number("ELEV"), 0);

        // and the clock, through the mount command directly
        Assert.True((await Commands.CallAsync(node, EquipmentIds.Command(DeviceKinds.Mount, "Telescope_Simulator", SiteIds.MountSetTime), (BinaryConvertibleString)"2026-01-15T20:00:00Z")).Ok.Value);
        var time = await c.WaitForAsync("Telescope Simulator", "TIME_UTC", p => p.Text("UTC").StartsWith("2026-01-15T20:00"), TimeSpan.FromSeconds(30));
        Assert.StartsWith("2026-01-15T20:00", time.Text("UTC"));
    }
}
