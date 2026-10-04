namespace ELink.Core.Astro;

/// <summary>How an image area (the "plan", in the area's own axes: degrees along its width and height from its centre, turned by a
/// position angle) lies on the sky. Every part of ELink that places the area uses this one mapping, so the frame drawn on the chart,
/// the poses the scopes are sent to, the coverage cells and the stacked picture agree, also for large areas.
/// <para>A point at (x, y) degrees sits at the tangent-plane position (tan x, tan y) of the area's centre: that is how a
/// rectilinear camera sees it, so the area's edges are straight lines on a tangent plane (great circles on the sky, curved on a
/// chart) and its size along each axis through the centre is exactly the angle asked for. The turn is done on the tangent plane,
/// where it is a real rotation. For areas of a few degrees this is the same as adding angles; for tens of degrees it is what keeps
/// a turned area a rectangle.</para></summary>
public static class PlanProjection
{
    private const double D2R = Math.PI / 180;

    /// <summary>Sky position of the point (x, y) degrees from the centre in the area's axes; the area's up points <paramref name="positionAngleDegrees"/> east of north.</summary>
    public static (double RaHours, double DecDegrees) ToSky(double centerRaHours, double centerDecDegrees, double positionAngleDegrees, double x, double y)
    {
        double X = Math.Tan(Math.Clamp(x, -89.9, 89.9) * D2R), Y = Math.Tan(Math.Clamp(y, -89.9, 89.9) * D2R);
        double t = positionAngleDegrees * D2R, c = Math.Cos(t), s = Math.Sin(t);
        return Gnomonic.ToSkyPlane(centerRaHours, centerDecDegrees, X * c + Y * s, -X * s + Y * c);
    }

    /// <summary>The inverse: where a sky position lies in the area's axes (degrees); false when it is 90° or more from the centre.</summary>
    public static bool TryFromSky(double centerRaHours, double centerDecDegrees, double positionAngleDegrees, double raHours, double decDegrees, out double x, out double y)
    {
        x = y = double.NaN;
        if (!Gnomonic.TryFromSkyPlane(centerRaHours, centerDecDegrees, raHours, decDegrees, out var xi, out var eta)) return false;
        double t = positionAngleDegrees * D2R, c = Math.Cos(t), s = Math.Sin(t);
        x = Math.Atan(xi * c - eta * s) / D2R; y = Math.Atan(xi * s + eta * c) / D2R;
        return true;
    }

    /// <summary>The width, in tangent-plane degrees (radians scaled to degrees: the unit of a tangent grid with a pixel scale in arcseconds),
    /// that holds an area <paramref name="degrees"/> wide: 2·tan(half), so a stack grid of this width reaches exactly the area's edges.</summary>
    public static double TangentSpan(double degrees) => degrees >= 178 ? degrees : 2 * Math.Tan(degrees / 2 * D2R) / D2R;
}
