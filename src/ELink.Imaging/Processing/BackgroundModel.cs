namespace ELink.Imaging.Processing;

/// <summary>A smooth model of the sky background: the sky is sampled in a grid of boxes (the faint pixels of each, away from stars), a
/// polynomial is fitted to the samples, and samples that sit above it (nebulosity, galaxies) are left out and the fit is made again.</summary>
public static class BackgroundModel
{
    /// <summary>The model for every pixel (null when there is too little to fit), the typical sky level and how far the model varies over the image.</summary>
    public static float[]? Fit(ReadOnlySpan<float> plane, int width, int height, int degree, out double level, out double span)
    {
        level = 0; span = 0;
        int gx = Math.Clamp(width / 40, 6, 20), gy = Math.Clamp((int)Math.Round(gx * (double)height / width), 6, 20);
        var xs = new List<double>(); var ys = new List<double>(); var vs = new List<double>();
        for (int j = 0; j < gy; j++)
            for (int i = 0; i < gx; i++)
            {
                int x0 = i * width / gx, x1 = (i + 1) * width / gx, y0 = j * height / gy, y1 = (j + 1) * height / gy;
                double v = BoxLevel(plane, width, x0, x1, y0, y1);
                if (double.IsNaN(v)) continue;
                xs.Add(((x0 + x1) / 2.0 / width) * 2 - 1); ys.Add(((y0 + y1) / 2.0 / height) * 2 - 1); vs.Add(v);
            }
        int terms = (degree + 1) * (degree + 2) / 2;
        if (vs.Count < terms + 4) return null;

        var weights = Enumerable.Repeat(1.0, vs.Count).ToArray();
        double[] coef = new double[terms];
        for (int pass = 0; pass < 6; pass++)
        {
            if (!Solve(xs, ys, vs, weights, degree, coef)) return null;
            var res = new double[vs.Count];
            for (int k = 0; k < vs.Count; k++) res[k] = vs[k] - Eval(coef, degree, xs[k], ys[k]);
            var used = res.Where((r, k) => weights[k] > 0).Select(Math.Abs).OrderBy(x => x).ToList();
            double sigma = Math.Max(1.4826 * used[used.Count / 2], 1e-9);
            for (int k = 0; k < vs.Count; k++) weights[k] = res[k] > 1.2 * sigma || res[k] < -3 * sigma ? 0 : 1;   // sky is not brighter than the model
            if (weights.Sum() < terms + 2) break;
        }

        // the model, row by row (powers of x per column are reused)
        var model = new float[width * height];
        var px = new double[width * (degree + 1)];
        for (int x = 0; x < width; x++) { double u = (x + 0.5) / width * 2 - 1, pw = 1; for (int a = 0; a <= degree; a++) { px[x * (degree + 1) + a] = pw; pw *= u; } }
        double min = double.MaxValue, max = double.MinValue; var levels = new List<double>();
        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height * 2 - 1;
            var py = new double[degree + 1]; double q = 1; for (int b = 0; b <= degree; b++) { py[b] = q; q *= v; }
            for (int x = 0; x < width; x++)
            {
                double s = 0; int t = 0;
                for (int total = 0; total <= degree; total++)
                    for (int a = total; a >= 0; a--) { s += coef[t++] * px[x * (degree + 1) + a] * py[total - a]; }
                model[y * width + x] = (float)s;
                if (((x | y) & 7) == 0) { min = Math.Min(min, s); max = Math.Max(max, s); levels.Add(s); }
            }
        }
        levels.Sort();
        level = levels[levels.Count / 2]; span = max - min;
        return model;
    }

    /// <summary>The sky in a box: the median of the faint half of its pixels (stars and nebulosity are the bright ones).</summary>
    private static double BoxLevel(ReadOnlySpan<float> plane, int width, int x0, int x1, int y0, int y1)
    {
        var vals = new List<float>();
        int stepX = Math.Max(1, (x1 - x0) / 20), stepY = Math.Max(1, (y1 - y0) / 20);
        for (int y = y0; y < y1; y += stepY)
            for (int x = x0; x < x1; x += stepX) { float v = plane[y * width + x]; if (!float.IsNaN(v)) vals.Add(v); }
        if (vals.Count < 20) return double.NaN;
        vals.Sort();
        double med = vals[vals.Count / 2], mad = Math.Max(med - vals[vals.Count / 4], 1e-9);
        // clipped: keep what is within a couple of sigmas of the faint half
        var kept = vals.Where(v => v <= med + 1.5 * mad).ToList();
        return kept[kept.Count / 2];
    }

    private static double Eval(double[] coef, int degree, double x, double y)
    {
        double s = 0; int t = 0;
        for (int total = 0; total <= degree; total++)
            for (int a = total; a >= 0; a--) s += coef[t++] * Math.Pow(x, a) * Math.Pow(y, total - a);
        return s;
    }

    /// <summary>Weighted least squares by the normal equations.</summary>
    private static bool Solve(List<double> xs, List<double> ys, List<double> vs, double[] w, int degree, double[] coef)
    {
        int n = coef.Length;
        var ata = new double[n, n]; var atb = new double[n];
        var row = new double[n];
        for (int k = 0; k < vs.Count; k++)
        {
            if (w[k] <= 0) continue;
            int t = 0;
            for (int total = 0; total <= degree; total++)
                for (int a = total; a >= 0; a--) row[t++] = Math.Pow(xs[k], a) * Math.Pow(ys[k], total - a);
            for (int i = 0; i < n; i++) { atb[i] += row[i] * vs[k]; for (int j = 0; j < n; j++) ata[i, j] += row[i] * row[j]; }
        }
        for (int i = 0; i < n; i++) ata[i, i] += 1e-9;
        // Gaussian elimination with partial pivoting
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(ata[r, c]) > Math.Abs(ata[piv, c])) piv = r;
            if (Math.Abs(ata[piv, c]) < 1e-14) return false;
            if (piv != c) { for (int j = 0; j < n; j++) (ata[c, j], ata[piv, j]) = (ata[piv, j], ata[c, j]); (atb[c], atb[piv]) = (atb[piv], atb[c]); }
            for (int r = c + 1; r < n; r++)
            {
                double f = ata[r, c] / ata[c, c];
                for (int j = c; j < n; j++) ata[r, j] -= f * ata[c, j];
                atb[r] -= f * atb[c];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = atb[i]; for (int j = i + 1; j < n; j++) s -= ata[i, j] * coef[j];
            coef[i] = s / ata[i, i];
        }
        return true;
    }
}
