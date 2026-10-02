namespace ELink.Core.Astro;

public enum Body { Sun, Moon, Mercury, Venus, Mars, Jupiter, Saturn, Uranus, Neptune }

/// <summary>A solar system body seen from Earth. RA/Dec J2000 (astrometric, no aberration or nutation), distance in AU,
/// illuminated fraction 0..1 (NaN for the Sun), elongation from the Sun in degrees.</summary>
public readonly record struct BodyPosition(Body Body, double RaHours, double DecDegrees, double DistanceAu, double Illumination, double ElongationDegrees);

/// <summary>Positions good to about an arcminute (planets, Sun) or a few arcminutes (Moon): enough to find, frame and
/// plan, not for occultation timing. Sun: Meeus ch. 25. Moon: the main terms of Meeus ch. 47. Planets: JPL's
/// approximate Keplerian elements (Standish), valid 1800-2050.</summary>
public static class SolarSystem
{
    private const double D2R = Math.PI / 180;
    private const double AuKm = 149597870.7;
    /// <summary>TT - UTC, close enough for this decade.</summary>
    private static readonly TimeSpan DeltaT = TimeSpan.FromSeconds(69.2);
    private const double ObliquityJ2000 = 23.4392911;

    public static readonly Body[] Planets = [Body.Mercury, Body.Venus, Body.Mars, Body.Jupiter, Body.Saturn, Body.Uranus, Body.Neptune];

    private static double T(DateTime utc) => (Precession.JulianDate(utc + DeltaT) - 2451545.0) / 36525.0;
    private static double Norm(double deg) { deg %= 360; return deg < 0 ? deg + 360 : deg; }

    public static BodyPosition Position(Body body, DateTime utc)
    {
        var sun = SunGeocentric(utc);
        var (x, y, z) = body switch
        {
            Body.Sun => sun,
            Body.Moon => MoonGeocentric(utc),
            _ => PlanetGeocentric(body, utc),
        };
        var (ra, dec, dist) = ToEquatorial(x, y, z);
        if (body == Body.Sun) return new(body, ra, dec, dist, double.NaN, 0);
        var (sra, sdec, sdist) = ToEquatorial(sun.X, sun.Y, sun.Z);
        double elong = Sky.SeparationDegrees(ra, dec, sra, sdec);
        // phase angle from the triangle Sun-Earth-body
        double r = Math.Sqrt(sdist * sdist + dist * dist - 2 * sdist * dist * Math.Cos(elong * D2R));
        double cosPhase = Math.Clamp((r * r + dist * dist - sdist * sdist) / (2 * r * dist), -1, 1);
        return new(body, ra, dec, dist, (1 + cosPhase) / 2, elong);
    }

    /// <summary>Moves a geocentric J2000 position to where an observer at <paramref name="site"/> sees it (parallax:
    /// up to a degree for the Moon, arcseconds for planets).</summary>
    public static (double RaHours, double DecDegrees) Topocentric(BodyPosition p, DateTime utc, GeoSite site)
    {
        var (raD, decD) = Precession.J2000ToDate(p.RaHours, p.DecDegrees, utc);
        // observer's geocentric position (Meeus ch. 11), in Earth radii
        double lat = site.LatitudeDegrees * D2R;
        double u = Math.Atan(0.99664719 * Math.Tan(lat));
        double rhoSin = 0.99664719 * Math.Sin(u) + site.ElevationMeters / 6378140 * Math.Sin(lat);
        double rhoCos = Math.Cos(u) + site.ElevationMeters / 6378140 * Math.Cos(lat);
        double sinPi = Math.Sin(8.794 / 3600 * D2R) / p.DistanceAu;
        double ha = Horizon.HourAngleHours(raD, utc, site.LongitudeDegrees) * 15 * D2R;
        double dec = decD * D2R;
        double dRa = Math.Atan2(-rhoCos * sinPi * Math.Sin(ha), Math.Cos(dec) - rhoCos * sinPi * Math.Cos(ha));
        double decT = Math.Atan2((Math.Sin(dec) - rhoSin * sinPi) * Math.Cos(dRa), Math.Cos(dec) - rhoCos * sinPi * Math.Cos(ha));
        return Precession.DateToJ2000(Precession.NormalizeHours(raD + dRa / D2R / 15), decT / D2R, utc);
    }

    /// <summary>Ecliptic J2000 rectangular (AU) to J2000 RA/Dec and distance.</summary>
    private static (double Ra, double Dec, double Dist) ToEquatorial(double x, double y, double z)
    {
        double e = ObliquityJ2000 * D2R;
        double xe = x, ye = y * Math.Cos(e) - z * Math.Sin(e), ze = y * Math.Sin(e) + z * Math.Cos(e);
        double dist = Math.Sqrt(xe * xe + ye * ye + ze * ze);
        return (Precession.NormalizeHours(Math.Atan2(ye, xe) / D2R / 15), Math.Asin(ze / dist) / D2R, dist);
    }

    /// <summary>Rotates ecliptic-of-date longitude/latitude into J2000 ecliptic rectangular coordinates.</summary>
    private static (double X, double Y, double Z) OfDateToJ2000(double lonDeg, double latDeg, double distAu, DateTime utc)
    {
        double t = T(utc);
        double eps = (23.4392911 - 0.0130042 * t) * D2R;
        double l = lonDeg * D2R, b = latDeg * D2R;
        // ecliptic of date -> equatorial of date -> J2000 equatorial -> J2000 ecliptic
        double xq = Math.Cos(b) * Math.Cos(l), yq = Math.Cos(b) * Math.Sin(l) * Math.Cos(eps) - Math.Sin(b) * Math.Sin(eps);
        double zq = Math.Cos(b) * Math.Sin(l) * Math.Sin(eps) + Math.Sin(b) * Math.Cos(eps);
        double ra = Math.Atan2(yq, xq) / D2R / 15, dec = Math.Asin(Math.Clamp(zq, -1, 1)) / D2R;
        var (ra0, dec0) = Precession.DateToJ2000(Precession.NormalizeHours(ra), dec, utc);
        double a = ra0 * 15 * D2R, d = dec0 * D2R, e0 = ObliquityJ2000 * D2R;
        double xe = Math.Cos(d) * Math.Cos(a), ye = Math.Cos(d) * Math.Sin(a), ze = Math.Sin(d);
        return (distAu * xe, distAu * (ye * Math.Cos(e0) + ze * Math.Sin(e0)), distAu * (-ye * Math.Sin(e0) + ze * Math.Cos(e0)));
    }

    // ---- Sun ----------------------------------------------------------------------------------------------------

    private static (double X, double Y, double Z) SunGeocentric(DateTime utc)
    {
        double t = T(utc);
        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * D2R;
        double e = 0.016708634 - 0.000042037 * t;
        double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m) + (0.019993 - 0.000101 * t) * Math.Sin(2 * m) + 0.000289 * Math.Sin(3 * m);
        double v = m + c * D2R;
        double r = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(v));
        return OfDateToJ2000(Norm(l0 + c), 0, r, utc);
    }

    // ---- Moon (Meeus ch. 47, main terms) -------------------------------------------------------------------------

    // D, M, M', F, sin coefficient for longitude (1e-6 deg), cos coefficient for distance (1e-3 km)
    private static readonly (int D, int M, int Mp, int F, double L, double R)[] MoonLr =
    [
        (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968),
        (0, 0, 2, 0, 213618, -569925), (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149),
        (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138), (2, 0, 1, 0, 53322, -170733),
        (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
        (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0),
        (0, 0, 1, -2, 10980, 79661), (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210),
        (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208), (2, 1, 0, 0, -6766, 30824),
        (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
        (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403),
        (0, 1, -2, 0, -2689, -7003), (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056),
        (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884),
    ];

    private static readonly (int D, int M, int Mp, int F, double B)[] MoonB =
    [
        (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
        (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
        (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
        (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
        (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
    ];

    /// <summary>Geocentric ecliptic longitude, latitude (of date, degrees) and distance (km) of the Moon.</summary>
    public static (double Longitude, double Latitude, double DistanceKm) MoonEcliptic(DateTime utc)
    {
        double t = T(utc);
        double lp = Norm(218.3164477 + 481267.88123421 * t - 0.0015786 * t * t);
        double d = Norm(297.8501921 + 445267.1114034 * t - 0.0018819 * t * t);
        double m = Norm(357.5291092 + 35999.0502909 * t - 0.0001536 * t * t);
        double mp = Norm(134.9633964 + 477198.8675055 * t + 0.0087414 * t * t);
        double f = Norm(93.2720950 + 483202.0175233 * t - 0.0036539 * t * t);
        double a1 = Norm(119.75 + 131.849 * t), a2 = Norm(53.09 + 479264.290 * t), a3 = Norm(313.45 + 481266.484 * t);
        double e = 1 - 0.002516 * t - 0.0000074 * t * t;
        double sl = 0, sr = 0, sb = 0;
        foreach (var k in MoonLr)
        {
            double arg = (k.D * d + k.M * m + k.Mp * mp + k.F * f) * D2R;
            double ef = Math.Abs(k.M) == 1 ? e : Math.Abs(k.M) == 2 ? e * e : 1;
            sl += k.L * ef * Math.Sin(arg); sr += k.R * ef * Math.Cos(arg);
        }
        foreach (var k in MoonB)
        {
            double arg = (k.D * d + k.M * m + k.Mp * mp + k.F * f) * D2R;
            double ef = Math.Abs(k.M) == 1 ? e : Math.Abs(k.M) == 2 ? e * e : 1;
            sb += k.B * ef * Math.Sin(arg);
        }
        sl += 3958 * Math.Sin(a1 * D2R) + 1962 * Math.Sin((lp - f) * D2R) + 318 * Math.Sin(a2 * D2R);
        sb += -2235 * Math.Sin(lp * D2R) + 382 * Math.Sin(a3 * D2R) + 175 * Math.Sin((a1 - f) * D2R) + 175 * Math.Sin((a1 + f) * D2R)
              + 127 * Math.Sin((lp - mp) * D2R) - 115 * Math.Sin((lp + mp) * D2R);
        return (Norm(lp + sl / 1e6), sb / 1e6, 385000.56 + sr / 1000);
    }

    private static (double X, double Y, double Z) MoonGeocentric(DateTime utc)
    {
        var (l, b, r) = MoonEcliptic(utc);
        return OfDateToJ2000(l, b, r / AuKm, utc);
    }

    // ---- planets ---------------------------------------------------------------------------------------------------

    // a (AU), e, I, L, long. perihelion, long. node (deg), then rates per century (Standish, Table 1, 1800-2050)
    private static readonly Dictionary<Body, double[]> Elements = new()
    {
        [Body.Mercury] = [0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593, 0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081],
        [Body.Venus] = [0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255, 0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418],
        [Body.Sun] = [1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0.0, 0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0.0],   // Earth-Moon barycentre
        [Body.Mars] = [1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891, 0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343],
        [Body.Jupiter] = [5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909, -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106],
        [Body.Saturn] = [9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448, -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794],
        [Body.Uranus] = [19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503, -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589],
        [Body.Neptune] = [30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574, 0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664],
    };

    /// <summary>Heliocentric J2000 ecliptic position (AU) from the approximate elements; <c>Body.Sun</c> means the Earth.</summary>
    private static (double X, double Y, double Z) Heliocentric(Body body, double t)
    {
        var k = Elements[body];
        double a = k[0] + k[6] * t, e = k[1] + k[7] * t, inc = (k[2] + k[8] * t) * D2R;
        double l = k[3] + k[9] * t, peri = k[4] + k[10] * t, node = k[5] + k[11] * t;
        double w = (peri - node) * D2R, mDeg = Norm(l - peri); if (mDeg > 180) mDeg -= 360;
        double mean = mDeg * D2R, ecc = mean + e * Math.Sin(mean);
        for (int i = 0; i < 8; i++) ecc -= (ecc - e * Math.Sin(ecc) - mean) / (1 - e * Math.Cos(ecc));
        double xp = a * (Math.Cos(ecc) - e), yp = a * Math.Sqrt(1 - e * e) * Math.Sin(ecc);
        double cw = Math.Cos(w), sw = Math.Sin(w), cn = Math.Cos(node * D2R), sn = Math.Sin(node * D2R), ci = Math.Cos(inc), si = Math.Sin(inc);
        return ((cw * cn - sw * sn * ci) * xp + (-sw * cn - cw * sn * ci) * yp,
                (cw * sn + sw * cn * ci) * xp + (-sw * sn + cw * cn * ci) * yp,
                sw * si * xp + cw * si * yp);
    }

    private static (double X, double Y, double Z) PlanetGeocentric(Body body, DateTime utc)
    {
        double t = T(utc);
        var earth = Heliocentric(Body.Sun, t);
        double tau = 0;
        (double X, double Y, double Z) g = default;
        for (int i = 0; i < 2; i++)   // light time
        {
            var p = Heliocentric(body, t - tau / 36525.0);
            g = (p.X - earth.X, p.Y - earth.Y, p.Z - earth.Z);
            tau = Math.Sqrt(g.X * g.X + g.Y * g.Y + g.Z * g.Z) * 0.0057755183;   // days per AU
        }
        return g;
    }

    public static bool TryParse(string text, out Body body) =>
        Enum.TryParse(text.Trim(), true, out body) && Enum.IsDefined(body);
}
