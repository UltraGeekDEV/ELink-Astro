using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests;

public class SolarSystemTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    // the ephemerides run on TT = UTC + 69.2 s; Meeus' examples are given in TT
    private static DateTime FromTT(DateTime tt) => tt - TimeSpan.FromSeconds(69.2);

    [Fact]
    public void SiderealTimeMatchesMeeus()
    {
        // Meeus example 12.a: 1987 April 10, 0h UT: GMST 13h10m46.3668s
        Assert.Equal(13 + 10 / 60.0 + 46.3668 / 3600, Horizon.GreenwichSiderealHours(Utc(1987, 4, 10)), 5);
        // 12.b: 19h21m00s UT the same day: 8h34m57.0896s
        Assert.Equal(8 + 34 / 60.0 + 57.0896 / 3600, Horizon.GreenwichSiderealHours(Utc(1987, 4, 10, 19, 21)), 5);
    }

    [Fact]
    public void AltAzAndBackAgree()
    {
        var site = new GeoSite(47.5, 19.05);
        var t = Utc(2026, 10, 2, 21);
        var (alt, az) = Horizon.AltAz(5.6, -5.4, t, site);
        var (ra, dec) = Horizon.FromAltAz(alt, az, t, site);
        Assert.Equal(5.6, ra, 6); Assert.Equal(-5.4, dec, 6);
        // the celestial pole stands at the latitude, due north
        var (palt, paz) = Horizon.AltAz(3, 90, t, site);
        Assert.Equal(47.5, palt, 6); Assert.True(paz < 1e-6 || paz > 360 - 1e-6);
        // an object on the meridian culminates at 90 - lat + dec, due south
        double lst = Horizon.LocalSiderealHours(t, site.LongitudeDegrees);
        var (malt, maz) = Horizon.AltAz(lst, 10, t, site);
        Assert.Equal(90 - 47.5 + 10, malt, 6); Assert.Equal(180, maz, 6);
    }

    [Fact]
    public void MoonMatchesMeeusExample()
    {
        // Meeus example 47.a: 1992 April 12, 0h TD: lambda 133.162655, beta -3.229126, distance 368409.7 km
        var (l, b, r) = SolarSystem.MoonEcliptic(FromTT(Utc(1992, 4, 12)));
        Assert.Equal(133.162655, l, 1);     // main terms only: a few arcminutes
        Assert.Equal(-3.229126, b, 1);
        Assert.InRange(r, 368409.7 - 300, 368409.7 + 300);
    }

    [Fact]
    public void SunMatchesMeeusExample()
    {
        // Meeus example 25.a: 1992 October 13, 0h TD: apparent RA 198.38083 deg, Dec -7.78507 (of date)
        var s = SolarSystem.Position(Body.Sun, FromTT(Utc(1992, 10, 13)));
        var (ra, dec) = Precession.J2000ToDate(s.RaHours, s.DecDegrees, Utc(1992, 10, 13));
        Assert.Equal(198.38083, ra * 15, 1);
        Assert.Equal(-7.78507, dec, 1);
        Assert.Equal(0.99766, s.DistanceAu, 3);
    }

    [Theory]
    [InlineData(Body.Jupiter, 2024, 12, 7)]   // oppositions: opposite the Sun
    [InlineData(Body.Mars, 2025, 1, 16)]
    [InlineData(Body.Saturn, 2024, 9, 8)]
    [InlineData(Body.Uranus, 2024, 11, 17)]
    [InlineData(Body.Neptune, 2024, 9, 21)]
    public void PlanetsAreOppositeTheSunAtOpposition(Body body, int y, int m, int d)
    {
        var p = SolarSystem.Position(body, Utc(y, m, d, 12));
        Assert.InRange(p.ElongationDegrees, body == Body.Mars ? 175 : 176, 180);   // Mars stood 4.5 deg off the ecliptic then
        Assert.InRange(p.Illumination, 0.99, 1.0);
    }

    [Fact]
    public void VenusAndMercuryGreatestElongations()
    {
        Assert.Equal(47.2, SolarSystem.Position(Body.Venus, Utc(2025, 1, 10)).ElongationDegrees, 0);       // greatest eastern elongation
        Assert.Equal(18.6, SolarSystem.Position(Body.Mercury, Utc(2025, 8, 19)).ElongationDegrees, 0);     // greatest western elongation
        Assert.InRange(SolarSystem.Position(Body.Venus, Utc(2025, 1, 10)).Illumination, 0.45, 0.55);       // half lit at greatest elongation
    }

    [Fact]
    public void MoonPhasesAndParallax()
    {
        // full moon 2025-03-14 06:55 UTC (total lunar eclipse), new moon 2025-03-29 10:58 UTC (partial solar eclipse)
        var full = SolarSystem.Position(Body.Moon, Utc(2025, 3, 14, 6, 55));
        Assert.InRange(full.Illumination, 0.995, 1.0);
        Assert.InRange(full.ElongationDegrees, 178.5, 180);   // an eclipse: right opposite the Sun
        var newMoon = SolarSystem.Position(Body.Moon, Utc(2025, 3, 29, 10, 58));
        Assert.InRange(newMoon.Illumination, 0, 0.005);
        Assert.InRange(newMoon.DistanceAu * 149597870.7, 356000, 407000);

        // parallax: seen from the equator with the Moon on the horizon it is about a degree lower; at the zenith, unchanged
        var t = Utc(2026, 10, 2, 20);
        var geo = SolarSystem.Position(Body.Moon, t);
        var (raD, decD) = Precession.J2000ToDate(geo.RaHours, geo.DecDegrees, t);
        double lon = (raD - Horizon.GreenwichSiderealHours(t)) * 15;                          // the Moon overhead here
        var top = SolarSystem.Topocentric(geo, t, new GeoSite(decD, lon));
        Assert.True(Sky.SeparationDegrees(top.RaHours, top.DecDegrees, geo.RaHours, geo.DecDegrees) < 0.01);
        var top2 = SolarSystem.Topocentric(geo, t, new GeoSite(decD, lon + 90));                // on the horizon
        Assert.InRange(Sky.SeparationDegrees(top2.RaHours, top2.DecDegrees, geo.RaHours, geo.DecDegrees), 0.85, 1.0);
    }

    [Fact]
    public void SunriseSunsetAndTwilight()
    {
        // Budapest, 2025-06-21: sunrise 02:46, sunset 18:45 UTC (CEST - 2), give or take a minute or two
        var site = new GeoSite(47.4979, 19.0402, 100);
        double SunAlt(DateTime t) { var s = SolarSystem.Position(Body.Sun, t); return Horizon.AltAzJ2000(s.RaHours, s.DecDegrees, t, site).Altitude; }
        var ev = Horizon.Events(SunAlt, -0.833, Utc(2025, 6, 21), Utc(2025, 6, 22));
        var rise = ev.Single(e => e.Kind == Horizon.EventKind.Rise).Utc;
        var set = ev.Single(e => e.Kind == Horizon.EventKind.Set).Utc;
        var noon = ev.Single(e => e.Kind == Horizon.EventKind.Transit);
        Assert.InRange((rise - Utc(2025, 6, 21, 2, 46)).TotalMinutes, -3, 3);
        Assert.InRange((set - Utc(2025, 6, 21, 18, 45)).TotalMinutes, -3, 3);
        Assert.Equal(90 - 47.4979 + 23.44, noon.Altitude, 0);
        // at midsummer the Sun gets at most 90 - 47.5 - 23.44 = 19.06 degrees down in Budapest
        Assert.Contains(Horizon.Events(SunAlt, -18, Utc(2025, 6, 21, 12), Utc(2025, 6, 22, 12)), e => e.Kind == Horizon.EventKind.Set);
        Assert.DoesNotContain(Horizon.Events(SunAlt, -19.5, Utc(2025, 6, 21, 12), Utc(2025, 6, 22, 12)), e => e.Kind == Horizon.EventKind.Set);
    }
}
