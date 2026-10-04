namespace ELink.Core.Astro;

/// <summary>Tangent-plane (gnomonic) projection: offsets east/north of a centre on a flat patch of sky, and back.</summary>
public static class Gnomonic
{
    private const double D2R = Math.PI / 180.0;

    /// <summary>Sky position of a point offset (east, north) degrees from the tangent point, measured on the tangent plane.</summary>
    public static (double RaHours, double DecDegrees) ToSky(double centerRaHours, double centerDecDegrees, double eastDegrees, double northDegrees)
    {
        double a0 = centerRaHours * 15 * D2R, d0 = centerDecDegrees * D2R;
        double xi = Math.Tan(eastDegrees * D2R), eta = Math.Tan(northDegrees * D2R);
        double denom = Math.Cos(d0) - eta * Math.Sin(d0);
        double ra = a0 + Math.Atan2(xi, denom);
        double dec = Math.Atan2(Math.Sin(d0) + eta * Math.Cos(d0), Math.Sqrt(xi * xi + denom * denom));
        return (Precession.NormalizeHours(ra * 12 / Math.PI), dec / D2R);
    }

    /// <summary>Like <see cref="ToSky"/> but for a point given by its tangent-plane coordinates (xi east, eta north, as tangents: no
    /// angle in between), which is how a rectilinear camera sees the sky.</summary>
    public static (double RaHours, double DecDegrees) ToSkyPlane(double centerRaHours, double centerDecDegrees, double xi, double eta)
    {
        double a0 = centerRaHours * 15 * D2R, d0 = centerDecDegrees * D2R;
        double denom = Math.Cos(d0) - eta * Math.Sin(d0);
        double ra = a0 + Math.Atan2(xi, denom);
        double dec = Math.Atan2(Math.Sin(d0) + eta * Math.Cos(d0), Math.Sqrt(xi * xi + denom * denom));
        return (Precession.NormalizeHours(ra * 12 / Math.PI), dec / D2R);
    }

    /// <summary>The tangent-plane coordinates (xi east, eta north) of a sky position; false when it is 90° or more from the centre.</summary>
    public static bool TryFromSkyPlane(double centerRaHours, double centerDecDegrees, double raHours, double decDegrees, out double xi, out double eta)
    {
        double a0 = centerRaHours * 15 * D2R, d0 = centerDecDegrees * D2R, a = raHours * 15 * D2R, d = decDegrees * D2R;
        double cosc = Math.Sin(d0) * Math.Sin(d) + Math.Cos(d0) * Math.Cos(d) * Math.Cos(a - a0);
        if (cosc <= 1e-9) { xi = eta = double.NaN; return false; }
        xi = Math.Cos(d) * Math.Sin(a - a0) / cosc;
        eta = (Math.Cos(d0) * Math.Sin(d) - Math.Sin(d0) * Math.Cos(d) * Math.Cos(a - a0)) / cosc;
        return true;
    }

    /// <summary>The inverse: (east, north) degrees of a sky position on the tangent plane of the centre.</summary>
    public static (double EastDegrees, double NorthDegrees) FromSky(double centerRaHours, double centerDecDegrees, double raHours, double decDegrees)
    {
        double a0 = centerRaHours * 15 * D2R, d0 = centerDecDegrees * D2R, a = raHours * 15 * D2R, d = decDegrees * D2R;
        double cosc = Math.Sin(d0) * Math.Sin(d) + Math.Cos(d0) * Math.Cos(d) * Math.Cos(a - a0);
        double xi = Math.Cos(d) * Math.Sin(a - a0) / cosc;
        double eta = (Math.Cos(d0) * Math.Sin(d) - Math.Sin(d0) * Math.Cos(d) * Math.Cos(a - a0)) / cosc;
        return (Math.Atan(xi) / D2R, Math.Atan(eta) / D2R);
    }
}
