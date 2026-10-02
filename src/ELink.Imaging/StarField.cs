namespace ELink.Imaging;

/// <param name="Elongation">major over minor axis of the star's light (second moments): 1 = round, larger = trailed</param>
public readonly record struct Star(double X, double Y, double Flux, double Hfr, double Peak, double Elongation = 1.0);

/// <summary>Finds stars in a linear frame and measures them. Meant for focusing and for guiding-style checks: it
/// favours robustness over completeness (isolated, unsaturated, reasonably round stars).</summary>
public static class StarField
{
    public sealed record Result(IReadOnlyList<Star> Stars, double Background, double Noise)
    {
        public int Count => Stars.Count;
        /// <summary>Median half-flux radius in pixels: the focus quality number (smaller is sharper). NaN without stars.</summary>
        public double MedianHfr
        {
            get
            {
                if (Stars.Count == 0) return double.NaN;
                var h = Stars.Select(s => s.Hfr).Order().ToArray();
                return h.Length % 2 == 1 ? h[h.Length / 2] : (h[h.Length / 2 - 1] + h[h.Length / 2]) / 2;
            }
        }

        /// <summary>Median elongation: about 1 for round stars, well above for trailing (wind, a mount that slipped).</summary>
        public double MedianElongation
        {
            get
            {
                if (Stars.Count == 0) return double.NaN;
                var e = Stars.Select(s => s.Elongation).Order().ToArray();
                return e.Length % 2 == 1 ? e[e.Length / 2] : (e[e.Length / 2 - 1] + e[e.Length / 2]) / 2;
            }
        }
    }

    /// <param name="sigma">detection threshold above the background, in noise sigmas</param>
    /// <param name="maxStars">keep the brightest N</param>
    public static Result Detect(FitsImage img, double sigma = 6, int maxStars = 100)
    {
        int w = img.Width, h = img.Height;
        var data = img.Data.AsSpan(0, w * h);          // first channel only (mono or red)
        Background(data, img.Range, out double bg, out double noise);
        double threshold = bg + Math.Max(sigma * noise, 1e-9 * img.Range);
        double saturation = img.Range * 0.98;

        var visited = new bool[w * h];
        var stars = new List<Star>();
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int y0 = 1; y0 < h - 1; y0++)
            for (int x0 = 1; x0 < w - 1; x0++)
            {
                int i0 = y0 * w + x0;
                if (visited[i0] || data[i0] <= threshold) continue;
                // grow a blob of connected above-threshold pixels
                members.Clear(); stack.Push(i0); visited[i0] = true;
                bool edge = false;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); members.Add(i);
                    int x = i % w, y = i / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) edge = true;
                    if (members.Count > 4000) { edge = true; }     // a nebula or a streak, not a star
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int ni = ny * w + nx;
                            if (!visited[ni] && data[ni] > threshold) { visited[ni] = true; stack.Push(ni); }
                        }
                }
                if (edge || members.Count < 3) continue;
                if (Measure(data, w, h, members, bg, saturation) is { } star) stars.Add(star);
            }
        var best = stars.OrderByDescending(s => s.Flux).Take(maxStars).ToList();
        return new Result(best, bg, noise);
    }

    private static void Background(ReadOnlySpan<float> data, double range, out double median, out double noise)
    {
        const int bins = 65536;
        var hist = new int[bins];
        double scale = (bins - 1) / Math.Max(range, 1e-12);
        foreach (var v in data) hist[(int)Math.Clamp(v * scale, 0, bins - 1)]++;
        long total = data.Length;
        int Quantile(int[] hh, double q) { long t = (long)(total * q), a = 0; for (int i = 0; i < hh.Length; i++) { a += hh[i]; if (a > t) return i; } return hh.Length - 1; }
        int m = Quantile(hist, 0.5);
        var dev = new int[bins];
        for (int i = 0; i < bins; i++) dev[Math.Abs(i - m)] += hist[i];
        median = m / scale;
        noise = Math.Max(Quantile(dev, 0.5) * 1.4826 / scale, range / bins);   // never below one level
    }

    private static Star? Measure(ReadOnlySpan<float> data, int w, int h, List<int> blob, double bg, double saturation)
    {
        // centroid on the blob grown by a margin, background-subtracted
        int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0; double peak = 0;
        foreach (int i in blob)
        {
            int x = i % w, y = i / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            peak = Math.Max(peak, data[i]);
        }
        if (peak >= saturation) return null;
        int pad = Math.Max(3, (maxX - minX + maxY - minY) / 2);
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad); maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);

        double sum = 0, sx = 0, sy = 0;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double v = data[y * w + x] - bg;
                if (v <= 0) continue;
                sum += v; sx += v * x; sy += v * y;
            }
        if (sum <= 0) return null;
        double cx = sx / sum, cy = sy / sum;
        // shape: second moments of the light within the blob (the background-only margin would only add noise)
        double mxx = 0, myy = 0, mxy = 0, ms = 0;
        foreach (int i in blob)
        {
            double v = data[i] - bg;
            if (v <= 0) continue;
            double dx = i % w - cx, dy = i / w - cy;
            mxx += v * dx * dx; myy += v * dy * dy; mxy += v * dx * dy; ms += v;
        }
        double elongation = 1;
        if (ms > 0)
        {
            mxx /= ms; myy /= ms; mxy /= ms;
            double tr = mxx + myy, det = mxx * myy - mxy * mxy, disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
            double l1 = tr / 2 + disc, l2 = tr / 2 - disc;
            elongation = l2 > 1e-9 ? Math.Sqrt(l1 / l2) : 1;
        }

        // half-flux radius: radius containing half of the flux, from the radial cumulative sum
        var radial = new List<(double r, double v)>();
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double v = data[y * w + x] - bg;
                if (v <= 0) continue;
                radial.Add((Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)), v));
            }
        radial.Sort((a, b) => a.r.CompareTo(b.r));
        double acc = 0, hfr = radial[^1].r;
        double prevR = 0, prevAcc = 0;
        foreach (var (r, v) in radial)
        {
            acc += v;
            if (acc >= sum / 2)
            {
                // interpolate inside this pixel ring
                double frac = acc - prevAcc > 0 ? (sum / 2 - prevAcc) / (acc - prevAcc) : 0;
                hfr = prevR + frac * (r - prevR);
                break;
            }
            prevR = r; prevAcc = acc;
        }
        // Single hot pixels and cosmic rays have an HFR near zero: not stars.
        if (hfr < 0.4) return null;
        return new Star(cx, cy, sum, hfr, peak - bg, elongation);
    }
}
