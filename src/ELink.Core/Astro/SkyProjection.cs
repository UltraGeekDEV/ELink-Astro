namespace ELink.Core.Astro;

/// <summary>Stereographic projection of the sky onto a chart, as star charts draw it: north up, east to the left. Conformal, so
/// shapes stay right at any field of view; exact inverse for picking with the mouse.</summary>
public readonly struct SkyProjection
{
    private const double D2R = Math.PI / 180;
    public double CenterRaHours { get; }
    public double CenterDecDegrees { get; }
    public double FovDegrees { get; }
    public double Width { get; }
    public double Height { get; }
    /// <summary>Pixels per unit of the projection plane.</summary>
    public double Scale { get; }
    private readonly double _sin0, _cos0;

    /// <param name="fovDegrees">the angle the chart's width spans</param>
    public SkyProjection(double centerRaHours, double centerDecDegrees, double fovDegrees, double width, double height)
    {
        CenterRaHours = centerRaHours; CenterDecDegrees = Math.Clamp(centerDecDegrees, -90, 90);
        FovDegrees = Math.Clamp(fovDegrees, 0.01, 300); Width = width; Height = height;
        Scale = width / 2 / (2 * Math.Tan(FovDegrees / 4 * D2R));        // a point FOV/2 from the centre lands on the edge
        _sin0 = Math.Sin(CenterDecDegrees * D2R); _cos0 = Math.Cos(CenterDecDegrees * D2R);
    }

    /// <summary>Chart position of a sky position; false when it is on the far side (more than ~170° away).</summary>
    public bool TryProject(double raHours, double decDegrees, out double x, out double y)
    {
        double dRa = (raHours - CenterRaHours) * 15 * D2R, d = decDegrees * D2R;
        double sinD = Math.Sin(d), cosD = Math.Cos(d), cosDRa = Math.Cos(dRa);
        double denom = 1 + _sin0 * sinD + _cos0 * cosD * cosDRa;
        if (denom < 0.01) { x = y = 0; return false; }
        double k = 2 / denom;
        double px = k * cosD * Math.Sin(dRa), py = k * (_cos0 * sinD - _sin0 * cosD * cosDRa);
        x = Width / 2 - px * Scale;                                      // east (larger RA) to the left
        y = Height / 2 - py * Scale;                                     // north up
        return true;
    }

    /// <summary>Sky position under a chart position.</summary>
    public (double RaHours, double DecDegrees) Unproject(double x, double y)
    {
        double px = (Width / 2 - x) / Scale, py = (Height / 2 - y) / Scale;
        double rho = Math.Sqrt(px * px + py * py);
        if (rho < 1e-12) return (CenterRaHours, CenterDecDegrees);
        double c = 2 * Math.Atan(rho / 2), sinC = Math.Sin(c), cosC = Math.Cos(c);
        double dec = Math.Asin(Math.Clamp(cosC * _sin0 + py * sinC * _cos0 / rho, -1, 1));
        double ra = CenterRaHours * 15 * D2R + Math.Atan2(px * sinC, rho * _cos0 * cosC - py * _sin0 * sinC);
        return (Precession.NormalizeHours(ra / D2R / 15), dec / D2R);
    }

    /// <summary>Pixels per degree at the centre of the chart.</summary>
    public double PixelsPerDegree => Scale * D2R;

    /// <summary>Half the diagonal of the chart, in degrees: what a query must cover.</summary>
    public double RadiusDegrees
    {
        get
        {
            var corner = Unproject(0, 0);
            return Sky.SeparationDegrees(CenterRaHours, CenterDecDegrees, corner.RaHours, corner.DecDegrees);
        }
    }
}
