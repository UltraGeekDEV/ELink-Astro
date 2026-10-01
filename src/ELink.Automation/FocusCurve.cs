namespace ELink.Automation;

/// <summary>Finds the best focus from (position, HFR) samples. Near focus the star radius follows a hyperbola,
/// HFR² = a² + b²(x - c)², so HFR² is exactly a parabola in position: fit that, the vertex is the best focus.</summary>
public static class FocusCurve
{
    public sealed record Fit(double BestPosition, double BestHfr, bool Valid, double RSquared);

    public static Fit Parabola(IReadOnlyList<(double Position, double Hfr)> points)
    {
        var p = points.Where(q => !double.IsNaN(q.Hfr) && q.Hfr > 0).ToList();
        if (p.Count < 3) return new Fit(double.NaN, double.NaN, false, 0);
        // centre positions for numerical stability: x in thousands of steps is plenty, but be careful anyway
        double mx = p.Average(q => q.Position);
        double sx = Math.Max(p.Max(q => q.Position) - p.Min(q => q.Position), 1);
        var xs = p.Select(q => (q.Position - mx) / sx).ToArray();
        var ys = p.Select(q => q.Hfr * q.Hfr).ToArray();

        // least squares y = A x² + B x + C via the normal equations
        double s0 = xs.Length, s1 = xs.Sum(), s2 = xs.Sum(x => x * x), s3 = xs.Sum(x => x * x * x), s4 = xs.Sum(x => x * x * x * x);
        double t0 = ys.Sum(), t1 = xs.Zip(ys, (x, y) => x * y).Sum(), t2 = xs.Zip(ys, (x, y) => x * x * y).Sum();
        var m = new[,] { { s4, s3, s2, t2 }, { s3, s2, s1, t1 }, { s2, s1, s0, t0 } };
        if (!Solve3(m, out double A, out double B, out double C)) return new Fit(double.NaN, double.NaN, false, 0);
        if (A <= 0) return new Fit(double.NaN, double.NaN, false, 0);          // no minimum: a slope or a hill

        double xv = -B / (2 * A);
        double minY = A * xv * xv + B * xv + C;
        double mean = ys.Average();
        double ssTot = ys.Sum(y => (y - mean) * (y - mean)), ssRes = xs.Zip(ys, (x, y) => { double f = A * x * x + B * x + C; return (y - f) * (y - f); }).Sum();
        double r2 = ssTot > 0 ? 1 - ssRes / ssTot : 1;
        return new Fit(mx + xv * sx, Math.Sqrt(Math.Max(minY, 0)), true, r2);
    }

    private static bool Solve3(double[,] m, out double a, out double b, out double c)
    {
        a = b = c = 0;
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-12) return false;
            if (piv != col) for (int k = 0; k < 4; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            for (int r = col + 1; r < 3; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int k = col; k < 4; k++) m[r, k] -= f * m[col, k];
            }
        }
        c = m[2, 3] / m[2, 2];
        b = (m[1, 3] - m[1, 2] * c) / m[1, 1];
        a = (m[0, 3] - m[0, 2] * c - m[0, 1] * b) / m[0, 0];
        return true;
    }
}
