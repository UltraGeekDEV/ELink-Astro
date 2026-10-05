namespace ELink.Imaging.Processing;

/// <summary>What to do to a linear stack to make a picture of it.</summary>
public sealed record ProcessingParams
{
    /// <summary>Stretch the data (a midtones transfer function), or only scale it linearly.</summary>
    public bool Stretch { get; init; } = true;
    /// <summary>Where the sky background ends up in the picture, 0..1 (0.25 is a dark grey sky; higher is brighter, shows more faint things).</summary>
    public double BackgroundLevel { get; init; } = 0.25;
    /// <summary>The black point is this many noise sigmas below the sky level.</summary>
    public double BlackClipSigmas { get; init; } = 2.8;
    /// <summary>One stretch for all colours (keeps the colour balance) or each colour on its own (neutral sky, may shift colours).</summary>
    public bool Linked { get; init; } = true;
    public bool RemoveGradient { get; init; } = true;
    /// <summary>How flexible the background model is: the degree of the polynomial, 1 (a tilt) .. 6.</summary>
    public int GradientDegree { get; init; } = 3;
    /// <summary>Divide by the model instead of subtracting it (vignetting rather than sky glow).</summary>
    public bool GradientDivide { get; init; }
    public bool NeutralizeBackground { get; init; } = true;
    /// <summary>1 = as it is, 0 = grey, above 1 = more colour.</summary>
    public double Saturation { get; init; } = 1.0;
    /// <summary>Takes away a green cast around the sky: 0..1.</summary>
    public double GreenReduction { get; init; }
    /// <summary>The white point: this fraction of the pixels may be clipped to white (0.0005 = the brightest 0.05 %).</summary>
    public double WhiteClipFraction { get; init; } = 0.0002;
}

public sealed record ProcessedPicture(float[] Display, int Width, int Height, int Channels, double GradientPercent, double BackgroundLevel, double[] Histogram, string Note);

/// <summary>Turns the linear stack into a picture: gradient removal, background neutralisation, an automatic stretch, colour touches.
/// Works on planar float data; pixels that are NaN (nothing there) are treated as sky.</summary>
public static class PictureProcessor
{
    public const int HistogramBins = 128;

    public static ProcessedPicture Process(float[] data, int width, int height, int channels, ProcessingParams p)
    {
        int plane = width * height;
        if (data.Length < plane * channels) throw new ArgumentException("data does not fit the size");
        var d = new float[plane * channels];
        Array.Copy(data, d, d.Length);
        var notes = new List<string>();

        // nothing-there pixels become the sky level of their channel
        for (int c = 0; c < channels; c++)
        {
            var span = d.AsSpan(c * plane, plane);
            float sky = Background(span);
            for (int i = 0; i < span.Length; i++) if (float.IsNaN(span[i]) || float.IsInfinity(span[i])) span[i] = sky;
        }

        double gradient = 0;
        if (p.RemoveGradient)
        {
            double worst = 0;
            for (int c = 0; c < channels; c++)
            {
                var model = BackgroundModel.Fit(d.AsSpan(c * plane, plane), width, height, Math.Clamp(p.GradientDegree, 1, 6), out double level, out double span);
                if (model is null) continue;
                var ch = d.AsSpan(c * plane, plane);
                if (p.GradientDivide) for (int i = 0; i < plane; i++) ch[i] = (float)(ch[i] / Math.Max(model[i] / level, 0.05));
                else for (int i = 0; i < plane; i++) ch[i] = (float)(ch[i] - model[i] + level);
                worst = Math.Max(worst, level > 0 ? span / level * 100 : 0);
            }
            gradient = worst;
            notes.Add(FormattableString.Invariant($"gradient {gradient:0.#}% of the sky level removed"));
        }

        var bg = new double[channels];
        for (int c = 0; c < channels; c++) bg[c] = Background(d.AsSpan(c * plane, plane));
        if (p.NeutralizeBackground && channels == 3)
        {
            double mean = bg.Average();
            for (int c = 0; c < 3; c++)
            {
                float shift = (float)(mean - bg[c]);
                var ch = d.AsSpan(c * plane, plane);
                for (int i = 0; i < plane; i++) ch[i] += shift;
                bg[c] = mean;
            }
            notes.Add("sky made neutral");
        }

        // the stretch
        var median = new double[channels]; var sigma = new double[channels]; var white = new double[channels];
        for (int c = 0; c < channels; c++)
        {
            Statistics(d.AsSpan(c * plane, plane), out median[c], out sigma[c], out white[c], p.WhiteClipFraction);
        }
        var black = new double[channels]; var hi = new double[channels]; var mid = new double[channels];
        for (int c = 0; c < channels; c++)
        {
            int src = p.Linked ? -1 : c;
            double m = src < 0 ? median.Average() : median[c], s = src < 0 ? sigma.Average() : sigma[c], w = src < 0 ? white.Max() : white[c];
            black[c] = m - p.BlackClipSigmas * s;
            hi[c] = Math.Max(w, black[c] + 1e-6);
            double xm = Math.Clamp((m - black[c]) / (hi[c] - black[c]), 1e-6, 0.999);
            mid[c] = p.Stretch ? Mtf(Math.Clamp(p.BackgroundLevel, 0.02, 0.9), xm) : 0.5;
        }
        var outp = new float[plane * channels];
        for (int c = 0; c < channels; c++)
        {
            double k = 1.0 / (hi[c] - black[c]);
            for (int i = 0; i < plane; i++)
            {
                double x = (d[c * plane + i] - black[c]) * k;
                outp[c * plane + i] = (float)(p.Stretch ? Mtf(mid[c], x) : Math.Clamp(x, 0, 1));
            }
        }
        if (p.Stretch) notes.Add(FormattableString.Invariant($"stretched, sky at {p.BackgroundLevel:0.00}"));

        if (channels == 3)
        {
            if (Math.Abs(p.Saturation - 1) > 1e-3)
            {
                for (int i = 0; i < plane; i++)
                {
                    float r = outp[i], g = outp[plane + i], b = outp[2 * plane + i];
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b, s = (float)p.Saturation;
                    outp[i] = Math.Clamp(l + (r - l) * s, 0f, 1f); outp[plane + i] = Math.Clamp(l + (g - l) * s, 0f, 1f); outp[2 * plane + i] = Math.Clamp(l + (b - l) * s, 0f, 1f);
                }
            }
            if (p.GreenReduction > 1e-3)
            {
                float a = (float)Math.Clamp(p.GreenReduction, 0, 1);
                for (int i = 0; i < plane; i++)
                {
                    float neutral = (outp[i] + outp[2 * plane + i]) / 2;
                    if (outp[plane + i] > neutral) outp[plane + i] -= a * (outp[plane + i] - neutral);
                }
            }
        }

        // what the result looks like: how the picture's values are spread
        var hist = new double[HistogramBins * channels];
        for (int c = 0; c < channels; c++)
            for (int i = 0; i < plane; i++) hist[c * HistogramBins + Math.Clamp((int)(outp[c * plane + i] * (HistogramBins - 1) + 0.5f), 0, HistogramBins - 1)]++;
        for (int c = 0; c < channels; c++)
        {
            double peak = 0; for (int b = 1; b < HistogramBins - 1; b++) peak = Math.Max(peak, hist[c * HistogramBins + b]);   // (the ends are mostly clipped pixels)
            peak = Math.Max(peak, 1);
            for (int b = 0; b < HistogramBins; b++) hist[c * HistogramBins + b] = Math.Min(1, hist[c * HistogramBins + b] / peak);
        }
        return new ProcessedPicture(outp, width, height, channels, gradient, bg.Average(), hist, string.Join(", ", notes));
    }

    /// <summary>Midtones transfer function: 0 stays 0, 1 stays 1, <paramref name="m"/> goes to 0.5.</summary>
    public static double Mtf(double m, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (Math.Abs(m - 0.5) < 1e-9) return x;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    /// <summary>The sky level of a plane: the clipped median of a sample of its pixels.</summary>
    public static float Background(ReadOnlySpan<float> plane)
    {
        var s = Sample(plane, 40000);
        if (s.Count == 0) return 0;
        s.Sort();
        double med = s[s.Count / 2];
        // stars and nebulae pull the median up: look at the lower part
        double lo = s[s.Count / 4], mad = Math.Max(s[s.Count / 2] - lo, 1e-9);
        var kept = s.Where(v => v <= med + 1.0 * mad).ToList();
        return kept.Count > 0 ? kept[kept.Count / 2] : (float)med;
    }

    /// <summary>The sky's median and noise (a robust sigma) and the white point (the value that this fraction of the pixels exceed).</summary>
    private static void Statistics(ReadOnlySpan<float> plane, out double median, out double sigma, out double white, double clipFraction)
    {
        var s = Sample(plane, 120000);
        s.Sort();
        double med = s[s.Count / 2];
        median = med;
        var dev = s.Select(v => Math.Abs(v - med)).ToList(); dev.Sort();
        sigma = Math.Max(dev[dev.Count / 2] * 1.4826, 1e-9);
        white = s[Math.Clamp((int)((1 - clipFraction) * (s.Count - 1)), 0, s.Count - 1)];
    }

    private static List<float> Sample(ReadOnlySpan<float> plane, int max)
    {
        int step = Math.Max(1, plane.Length / max);
        var list = new List<float>(plane.Length / step + 1);
        for (int i = 0; i < plane.Length; i += step) { float v = plane[i]; if (!float.IsNaN(v) && !float.IsInfinity(v)) list.Add(v); }
        return list;
    }
}
