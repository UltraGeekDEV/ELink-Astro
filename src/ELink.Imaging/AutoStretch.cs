namespace ELink.Imaging;

/// <summary>Screen stretch for linear astro data (the "screen transfer function" idea of PixInsight/N.I.N.A.):
/// clips the shadows at median - 2.8 MAD and lifts the midtones so the sky background sits at a fixed grey.
/// Display only: the data is never modified.</summary>
public static class AutoStretch
{
    public const double ShadowClipSigmas = -2.8;
    public const double TargetBackground = 0.25;

    public readonly record struct Params(double Shadows, double Midtones, double Highlights);

    /// <summary>Statistics-based parameters for one channel, values normalised to 0..1 by <paramref name="range"/>.</summary>
    public static Params Compute(ReadOnlySpan<float> channel, double range)
    {
        const int bins = 65536;
        var hist = new int[bins];
        double scale = (bins - 1) / range;
        foreach (var v in channel) hist[(int)Math.Clamp(v * scale, 0, bins - 1)]++;
        double median = Quantile(hist, channel.Length, 0.5) / (bins - 1);

        // median absolute deviation, from the histogram
        var dev = new int[bins];
        int m = (int)Math.Round(median * (bins - 1));
        for (int i = 0; i < bins; i++) dev[Math.Abs(i - m)] += hist[i];
        double mad = Quantile(dev, channel.Length, 0.5) / (bins - 1);
        double sigma = Math.Max(mad * 1.4826, 1e-6);

        double shadows = Math.Clamp(median + ShadowClipSigmas * sigma, 0, 1);
        double mid = Mtf(TargetBackground, median - shadows);
        return new Params(shadows, double.IsNaN(mid) || mid <= 0 ? 0.5 : mid, 1.0);
    }

    private static double Quantile(int[] hist, long total, double q)
    {
        long target = (long)(total * q), acc = 0;
        for (int i = 0; i < hist.Length; i++) { acc += hist[i]; if (acc > target) return i; }
        return hist.Length - 1;
    }

    /// <summary>Midtones transfer function: maps 0 to 0, m to 0.5 and 1 to 1.</summary>
    public static double Mtf(double m, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (m == 0.5) return x;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    /// <summary>Renders to premultiplied BGRA8888, top row first (FITS rows run bottom-up unless ROWORDER says TOP-DOWN), channels stretched
    /// independently when the image has three, linked on the luminance statistics otherwise.</summary>
    public static byte[] ToBgra(FitsImage img)
    {
        int w = img.Width, h = img.Height, plane = w * h;
        var p = new Params[Math.Min(img.Channels, 3)];
        for (int c = 0; c < p.Length; c++) p[c] = Compute(img.Data.AsSpan(c * plane, plane), img.Range);
        var lut = new byte[p.Length][];
        const int steps = 65535;   // one entry per 16-bit level: dim frames live in the lowest few dozen levels
        for (int c = 0; c < p.Length; c++)
        {
            lut[c] = new byte[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double x = (i / (double)steps - p[c].Shadows) / Math.Max(p[c].Highlights - p[c].Shadows, 1e-9);
                lut[c][i] = (byte)Math.Round(Mtf(p[c].Midtones, x) * 255);
            }
        }
        var bgra = new byte[plane * 4];
        for (int y = 0; y < h; y++)
        {
            int srcRow = (img.TopDown ? y : h - 1 - y) * w, dstRow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                byte r, g, b;
                if (img.Channels >= 3)
                {
                    r = Sample(img, lut[0], 0, srcRow + x); g = Sample(img, lut[1], 1, srcRow + x); b = Sample(img, lut[2], 2, srcRow + x);
                }
                else r = g = b = Sample(img, lut[0], 0, srcRow + x);
                int o = dstRow + x * 4;
                bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    private static byte Sample(FitsImage img, byte[] lut, int channel, int index)
    {
        double v = img.Data[channel * img.Width * img.Height + index] / img.Range;
        return lut[(int)Math.Clamp(v * 65535, 0, 65535)];
    }
}
