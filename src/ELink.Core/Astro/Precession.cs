namespace ELink.Core.Astro;

/// <summary>Converts equatorial coordinates between J2000 and the mean equinox of date ("JNow", what most INDI
/// mounts speak). IAU 1976 precession (Meeus ch. 21); nutation and aberration are not applied, so results are
/// good to roughly 20 arcseconds: fine for pointing, not for astrometry.</summary>
public static class Precession
{
    private const double ArcsecToRad = Math.PI / 180.0 / 3600.0;

    public static double JulianDate(DateTime utc) => utc.ToUniversalTime().Ticks / 864000000000.0 + 1721425.5;

    /// <summary>Julian centuries from J2000.0.</summary>
    public static double CenturiesSinceJ2000(DateTime utc) => (JulianDate(utc) - 2451545.0) / 36525.0;

    /// <returns>(ra hours, dec degrees) for the equinox of <paramref name="utc"/></returns>
    public static (double RaHours, double DecDegrees) J2000ToDate(double raHours, double decDegrees, DateTime utc)
    {
        double t = CenturiesSinceJ2000(utc);
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
        double z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;

        double a0 = raHours * Math.PI / 12.0, d0 = decDegrees * Math.PI / 180.0;
        double a = Math.Cos(d0) * Math.Sin(a0 + zeta);
        double b = Math.Cos(theta) * Math.Cos(d0) * Math.Cos(a0 + zeta) - Math.Sin(theta) * Math.Sin(d0);
        double c = Math.Sin(theta) * Math.Cos(d0) * Math.Cos(a0 + zeta) + Math.Cos(theta) * Math.Sin(d0);
        double ra = Math.Atan2(a, b) + z;
        double dec = Math.Abs(c) > 0.9999999 ? Math.Sign(c) * Math.Acos(Math.Sqrt(a * a + b * b)) : Math.Asin(c);
        return (NormalizeHours(ra * 12.0 / Math.PI), dec * 180.0 / Math.PI);
    }

    public static (double RaHours, double DecDegrees) DateToJ2000(double raHours, double decDegrees, DateTime utc)
    {
        // exact inverse of the forward rotations: undo z, then theta, then zeta
        double t = CenturiesSinceJ2000(utc);
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
        double z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;

        double a1 = raHours * Math.PI / 12.0 - z, d1 = decDegrees * Math.PI / 180.0;
        double x = Math.Cos(d1) * Math.Cos(a1), y = Math.Cos(d1) * Math.Sin(a1), zz = Math.Sin(d1);
        // undo the rotation about the y axis: x = cosθ·x' + sinθ·z', z = −sinθ·x' + cosθ·z'
        double x0 = Math.Cos(theta) * x + Math.Sin(theta) * zz;
        double z0 = -Math.Sin(theta) * x + Math.Cos(theta) * zz;
        double ra = Math.Atan2(y, x0) - zeta;
        double dec = Math.Abs(z0) > 0.9999999 ? Math.Sign(z0) * Math.Acos(Math.Sqrt(x0 * x0 + y * y)) : Math.Asin(z0);
        return (NormalizeHours(ra * 12.0 / Math.PI), dec * 180.0 / Math.PI);
    }

    public static double NormalizeHours(double h) { h %= 24.0; return h < 0 ? h + 24.0 : h; }
}
