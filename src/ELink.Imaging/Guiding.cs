namespace ELink.Imaging;

/// <summary>Follows a set of guide stars from frame to frame. The lock is up to a dozen bright, unsaturated, isolated
/// stars away from the edges; the field's shift is the median of their individual shifts, so one star lost to a cloud,
/// a hot pixel or a passing satellite does not move the result.</summary>
public sealed class StarTracker
{
    public IReadOnlyList<(double X, double Y)> Lock { get; }
    public int Width { get; }
    public int Height { get; }

    private StarTracker(IReadOnlyList<(double X, double Y)> locked, int width, int height) { Lock = locked; Width = width; Height = height; }

    /// <summary>Picks guide stars, or null when there is nothing usable.</summary>
    public static StarTracker? Acquire(IReadOnlyList<Star> stars, int width, int height, double saturation, int max = 12, double edge = 20, double isolation = 12)
    {
        var usable = stars
            .Where(s => s.Peak < saturation && s.Hfr > 0.3 && s.Hfr < 12 && s.X > edge && s.Y > edge && s.X < width - edge && s.Y < height - edge)
            .Where(s => !stars.Any(o => !ReferenceEquals(o, s) && !(o.X == s.X && o.Y == s.Y) && Math.Abs(o.X - s.X) < isolation && Math.Abs(o.Y - s.Y) < isolation))
            .OrderByDescending(s => s.Flux).Take(max).Select(s => (s.X, s.Y)).ToList();
        return usable.Count == 0 ? null : new StarTracker(usable, width, height);
    }

    /// <summary>Shift of the field since the lock (star position minus locked position), searching around the
    /// expected shift. Null when too few lock stars were found again.</summary>
    public (double Dx, double Dy, int Matched)? Measure(IReadOnlyList<Star> stars, double expectDx, double expectDy, double radius = 12)
    {
        var dx = new List<double>(); var dy = new List<double>();
        foreach (var (lx, ly) in Lock)
        {
            double px = lx + expectDx, py = ly + expectDy, best = radius * radius;
            Star? hit = null;
            foreach (var s in stars)
            {
                double d = (s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py);
                if (d < best) { best = d; hit = s; }
            }
            if (hit is { } h) { dx.Add(h.X - lx); dy.Add(h.Y - ly); }
        }
        int need = Math.Max(1, (int)Math.Ceiling(Lock.Count * 0.3));
        if (dx.Count < need) return null;
        return (Median(dx), Median(dy), dx.Count);
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }
}

/// <summary>What one millisecond of guide pulse does to the stars on the guide camera: <c>Ra</c> is the star motion
/// (pixels per ms) for a West pulse, <c>Dec</c> for a North pulse. A West pulse moves the pointing west, so the stars
/// move image-east; a North pulse makes them move image-south.</summary>
public sealed record PulseCalibration(double RaX, double RaY, double DecX, double DecY, double DecDegrees = double.NaN, string PierSide = "Unknown")
{
    public double RaRate => Math.Sqrt(RaX * RaX + RaY * RaY);
    public double DecRate => Math.Sqrt(DecX * DecX + DecY * DecY);

    /// <summary>Angle between the two axes as seen on the camera, degrees (90 for a healthy calibration).</summary>
    public double AxisAngleDegrees => Math.Acos(Math.Clamp((RaX * DecX + RaY * DecY) / (RaRate * DecRate), -1, 1)) * 180 / Math.PI;

    /// <summary>The RA rate scales with cos(declination): calibrated at one declination, guiding at another.</summary>
    private double RaScale(double decNow) =>
        double.IsNaN(decNow) || double.IsNaN(DecDegrees) || Math.Abs(decNow) > 85 || Math.Abs(DecDegrees) > 85
            ? 1 : Math.Cos(decNow * Math.PI / 180) / Math.Cos(DecDegrees * Math.PI / 180);

    /// <summary>Pulses (ms; + West / + North) that move the stars by (moveX, moveY) pixels.</summary>
    public (double RaMs, double DecMs) PulsesFor(double moveX, double moveY, double decNow = double.NaN)
    {
        double k = RaScale(decNow);
        double a = RaX * k, b = DecX, c = RaY * k, d = DecY;
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-12) return (0, 0);
        return ((d * moveX - b * moveY) / det, (-c * moveX + a * moveY) / det);
    }

    /// <summary>A star displacement split along the two axes, in pixels: how far the stars went image-east (the RA
    /// direction a West pulse pushes them) and image-south (the North-pulse direction).</summary>
    public (double EastPixels, double SouthPixels) Components(double ex, double ey)
    {
        // e = east * u_ra + south * u_dec
        double ux = RaX / RaRate, uy = RaY / RaRate, vx = DecX / DecRate, vy = DecY / DecRate;
        double det = ux * vy - vx * uy;
        if (Math.Abs(det) < 1e-12) return (0, 0);
        return ((ex * vy - vx * ey) / det, (ux * ey - uy * ex) / det);
    }
}
