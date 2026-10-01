namespace ELink.Core.Astro;

/// <summary>Small spherical-geometry helpers for pointing.</summary>
public static class Sky
{
    private const double D2R = Math.PI / 180.0;

    public static double SeparationDegrees(double ra1Hours, double dec1, double ra2Hours, double dec2)
    {
        double a1 = ra1Hours * 15 * D2R, a2 = ra2Hours * 15 * D2R, d1 = dec1 * D2R, d2 = dec2 * D2R;
        // haversine: stable for the tiny separations that matter here
        double s = Math.Sin((d2 - d1) / 2), t = Math.Sin((a2 - a1) / 2);
        double h = s * s + Math.Cos(d1) * Math.Cos(d2) * t * t;
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h))) / D2R;
    }

    /// <summary>Moves a position by a small offset (arcminutes east and north); fine up to a few degrees.</summary>
    public static (double RaHours, double DecDegrees) Offset(double raHours, double decDegrees, double eastArcmin, double northArcmin)
    {
        double dec = Math.Clamp(decDegrees + northArcmin / 60.0, -90, 90);
        double cos = Math.Max(Math.Cos(decDegrees * D2R), 1e-6);
        double ra = raHours + eastArcmin / 60.0 / 15.0 / cos;
        return (Precession.NormalizeHours(ra), dec);
    }
}
