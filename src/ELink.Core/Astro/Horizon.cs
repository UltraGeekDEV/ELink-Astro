namespace ELink.Core.Astro;

/// <summary>An observing site: geodetic latitude (north +), longitude (east +), elevation above sea level.</summary>
public readonly record struct GeoSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters = 0);

/// <summary>Sidereal time, horizon coordinates, and rise/transit/set times for any body.</summary>
public static class Horizon
{
    private const double D2R = Math.PI / 180;

    /// <summary>Greenwich mean sidereal time in hours (IAU 1982), from UT1 (taken as UTC: under a second off).</summary>
    public static double GreenwichSiderealHours(DateTime utc)
    {
        double jd = Precession.JulianDate(utc), t = (jd - 2451545.0) / 36525.0;
        double deg = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0;
        return Precession.NormalizeHours(deg / 15);
    }

    public static double LocalSiderealHours(DateTime utc, double longitudeDegrees) =>
        Precession.NormalizeHours(GreenwichSiderealHours(utc) + longitudeDegrees / 15);

    /// <summary>Hour angle in hours, -12..12 (negative: east of the meridian, still rising), for coordinates of date.</summary>
    public static double HourAngleHours(double raHoursOfDate, DateTime utc, double longitudeDegrees)
    {
        double h = LocalSiderealHours(utc, longitudeDegrees) - raHoursOfDate;
        h = Precession.NormalizeHours(h);
        return h > 12 ? h - 24 : h;
    }

    /// <summary>Geometric altitude and azimuth (from north through east) in degrees, for coordinates of date.</summary>
    public static (double Altitude, double Azimuth) AltAz(double raHoursOfDate, double decDegrees, DateTime utc, GeoSite site)
    {
        double ha = HourAngleHours(raHoursOfDate, utc, site.LongitudeDegrees) * 15 * D2R;
        double dec = decDegrees * D2R, lat = site.LatitudeDegrees * D2R;
        double sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
        double alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
        double az = Math.Atan2(-Math.Cos(dec) * Math.Sin(ha), Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha));
        return (alt / D2R, ((az / D2R) % 360 + 360) % 360);
    }

    /// <summary>Altitude of a J2000 position (precessed to the date first).</summary>
    public static (double Altitude, double Azimuth) AltAzJ2000(double raHours, double decDegrees, DateTime utc, GeoSite site)
    {
        var (ra, dec) = Precession.J2000ToDate(raHours, decDegrees, utc);
        return AltAz(ra, dec, utc, site);
    }

    /// <summary>Back from the horizon: coordinates of date of an altitude/azimuth.</summary>
    public static (double RaHoursOfDate, double DecDegrees) FromAltAz(double altitude, double azimuth, DateTime utc, GeoSite site)
    {
        double alt = altitude * D2R, az = azimuth * D2R, lat = site.LatitudeDegrees * D2R;
        double sinDec = Math.Sin(alt) * Math.Sin(lat) + Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az);
        double dec = Math.Asin(Math.Clamp(sinDec, -1, 1));
        double ha = Math.Atan2(-Math.Sin(az) * Math.Cos(alt), Math.Sin(alt) * Math.Cos(lat) - Math.Cos(alt) * Math.Sin(lat) * Math.Cos(az));
        double ra = LocalSiderealHours(utc, site.LongitudeDegrees) - ha / D2R / 15;
        return (Precession.NormalizeHours(ra), dec / D2R);
    }

    /// <summary>Atmospheric refraction in degrees for a true altitude (Saemundsson), for display and horizon checks.</summary>
    public static double RefractionDegrees(double trueAltitude)
    {
        if (trueAltitude < -1) return 0;
        return 1.02 / Math.Tan((trueAltitude + 10.3 / (trueAltitude + 5.11)) * D2R) / 60;
    }

    /// <summary>Lowest usable altitude at an azimuth: the flat minimum or a horizon profile (azimuth, altitude points,
    /// linear between neighbours, wrapping through north), whichever is higher.</summary>
    public static double ProfileAltitude(double flatMinimum, IReadOnlyList<(double Azimuth, double Altitude)> profile, double azimuth)
    {
        if (profile.Count == 0) return flatMinimum;
        var pts = profile.Select(p => (Az: ((p.Azimuth % 360) + 360) % 360, Alt: p.Altitude)).OrderBy(p => p.Az).ToList();
        if (pts.Count == 1) return Math.Max(flatMinimum, pts[0].Alt);
        azimuth = ((azimuth % 360) + 360) % 360;
        int i = pts.FindIndex(p => p.Az > azimuth);
        var (a, b) = i <= 0 ? (pts[^1], pts[0]) : (pts[i - 1], pts[i]);
        double span = ((b.Az - a.Az) % 360 + 360) % 360, from = ((azimuth - a.Az) % 360 + 360) % 360;
        double alt = span < 1e-9 ? a.Alt : a.Alt + (b.Alt - a.Alt) * from / span;
        return Math.Max(flatMinimum, alt);
    }

    public enum EventKind { Rise, Set, Transit }
    public readonly record struct SkyEvent(EventKind Kind, DateTime Utc, double Altitude);

    /// <summary>Rises and sets through <paramref name="altitude"/> and upper transits of a body whose altitude at a time is
    /// given by <paramref name="altitudeAt"/>, between <paramref name="fromUtc"/> and <paramref name="toUtc"/>. Found by
    /// stepping (10 minutes) and bisecting, so it works for anything, including the Moon.</summary>
    public static List<SkyEvent> Events(Func<DateTime, double> altitudeAt, double altitude, DateTime fromUtc, DateTime toUtc, TimeSpan? step = null)
    {
        var dt = step ?? TimeSpan.FromMinutes(10);
        var events = new List<SkyEvent>();
        DateTime t0 = fromUtc; double a0 = altitudeAt(t0), slope0 = double.NaN;
        while (t0 < toUtc)
        {
            var t1 = t0 + dt; if (t1 > toUtc) t1 = toUtc;
            double a1 = altitudeAt(t1);
            if ((a0 - altitude) * (a1 - altitude) < 0 || (a1 == altitude && a0 != altitude))
            {
                var t = Bisect(tt => altitudeAt(tt) - altitude, t0, t1);
                events.Add(new(a1 > a0 ? EventKind.Rise : EventKind.Set, t, altitude));
            }
            double slope1 = a1 - a0;
            if (!double.IsNaN(slope0) && slope0 > 0 && slope1 <= 0)
            {
                // the altitude peaked within the last two steps: find where it stops rising
                var tm = Bisect(tt => altitudeAt(tt + TimeSpan.FromSeconds(1)) - altitudeAt(tt), t0 - dt, t1);
                events.Add(new(EventKind.Transit, tm, altitudeAt(tm)));
            }
            slope0 = slope1; t0 = t1; a0 = a1;
        }
        events.Sort((x, y) => x.Utc.CompareTo(y.Utc));
        return events;
    }

    private static DateTime Bisect(Func<DateTime, double> f, DateTime lo, DateTime hi)
    {
        double flo = f(lo);
        for (int i = 0; i < 30 && (hi - lo).TotalSeconds > 1; i++)
        {
            var mid = lo + (hi - lo) / 2;
            double fm = f(mid);
            if ((fm > 0) == (flo > 0)) { lo = mid; flo = fm; } else hi = mid;
        }
        return lo + (hi - lo) / 2;
    }
}
