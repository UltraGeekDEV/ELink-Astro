namespace ELink.Imaging;

/// <summary>What a frame looks like: how many stars, how big, how round, how bright the sky.</summary>
public sealed record FrameMetrics(int Stars, double Hfr, double Elongation, double Background)
{
    public static FrameMetrics Measure(FitsImage img)
    {
        // a raw colour frame is a mosaic: every star is sampled by pixels of different colours, which makes round stars look long and ragged.
        // Measure it as the brightness of the 2x2 cells instead (sizes are given back in the sensor's pixels)
        double scale = 1;
        if (img.Channels == 1 && Debayer.PatternOf(img) is { } pattern && img.Width >= 8 && img.Height >= 8)
        {
            var c = Debayer.SuperPixel(img.Data, img.Width, img.Height, pattern);
            int plane = c.Width * c.Height; var luma = new float[plane];
            for (int i = 0; i < plane; i++) luma[i] = (c.Data[i] + 2 * c.Data[plane + i] + c.Data[2 * plane + i]) / 4;
            img = FitsImage.FromPlanar(c.Width, c.Height, 1, luma, img.Header, img.Range);
            scale = 2;
        }
        var r = StarField.Detect(img, 6, 300);
        return new FrameMetrics(r.Count, r.MedianHfr * scale, r.MedianElongation, r.Background);
    }
}

/// <summary>Grades the frames of one camera against what is normal for it right now: the median of its recent good
/// frames. Clouds take stars away, wind or a slipping mount makes them long, focus drift or bad seeing makes them big,
/// moonrise, dawn or a passing car's lights brighten the sky. The first frames (no baseline yet) only have to show stars.</summary>
public sealed class FrameGrader
{
    /// <summary>Reject below this share of the usual star count.</summary>
    public double MinStarsFraction { get; set; } = 0.4;
    /// <summary>Reject when stars are this much bigger than usual.</summary>
    public double MaxHfrRatio { get; set; } = 1.5;
    /// <summary>Reject when stars are this elongated (absolute: round is 1).</summary>
    public double MaxElongation { get; set; } = 1.6;
    /// <summary>Reject when the sky is this much brighter than usual.</summary>
    public double MaxBackgroundRatio { get; set; } = 2.5;
    /// <summary>How many recent good frames make the baseline.</summary>
    public int BaselineFrames { get; set; } = 10;

    /// <summary>After this many frames in a row that are all too big or too long by about the same amount, that is the new normal (the
    /// focus changed, the mount settled) and not a bad patch: they become the baseline.</summary>
    public int RebaselineAfter { get; set; } = 6;

    private readonly List<FrameMetrics> _good = new();
    private readonly List<FrameMetrics> _streak = new();

    public int BaselineCount => _good.Count;

    /// <summary>Forget the baseline (a new part of the sky: other stars, another sky brightness).</summary>
    public void Reset() { _good.Clear(); _streak.Clear(); }

    public (bool Ok, string Reason) Grade(FrameMetrics m)
    {
        if (m.Stars == 0) { _streak.Clear(); return (false, "no stars (clouds? the dome? the cap?)"); }
        double baselineElongation = _good.Count >= 3 ? Median(_good.Select(g => g.Elongation)) : double.NaN;
        // round stars are 1; a camera whose stars are always a little long (field rotation, a loose mount) is judged against its own usual
        double maxElongation = double.IsNaN(baselineElongation) ? MaxElongation : Math.Max(MaxElongation, 1.2 * baselineElongation);
        string? why = null;
        bool sizeOrShape = false;
        if (!double.IsNaN(m.Elongation) && m.Elongation > maxElongation) { why = FormattableString.Invariant($"stars trailed (elongation {m.Elongation:0.00})"); sizeOrShape = true; }
        if (_good.Count >= 3)
        {
            double stars = Median(_good.Select(g => (double)g.Stars)), hfr = Median(_good.Select(g => g.Hfr)), bg = Median(_good.Select(g => g.Background));
            // size and sky first: soft or washed-out frames also lose stars, and the cause is the more useful answer
            if (!double.IsNaN(hfr) && m.Hfr > MaxHfrRatio * hfr) { sizeOrShape = true; why ??= FormattableString.Invariant($"stars {m.Hfr / hfr:0.0}x their usual size (focus, seeing?)"); }
            else if (why is null && bg > 0 && m.Background > MaxBackgroundRatio * bg) why = FormattableString.Invariant($"sky {m.Background / bg:0.0}x brighter than usual");
            else if (why is null && m.Stars < MinStarsFraction * stars) why = FormattableString.Invariant($"{m.Stars} stars where {stars:0} are usual (clouds?)");
        }
        if (why is not null)
        {
            if (sizeOrShape)
            {
                _streak.Add(m);
                if (_streak.Count > RebaselineAfter) _streak.RemoveAt(0);
                // the same thing, frame after frame: stars of one size (within a fifth of each other): the baseline was what changed
                if (_streak.Count >= RebaselineAfter && _streak.Max(s => s.Hfr) <= 1.2 * _streak.Min(s => s.Hfr) && _streak.All(s => s.Stars >= MinStarsFraction * Math.Max(1, Median(_good.Select(g => (double)g.Stars)))))
                {
                    _good.Clear(); _good.AddRange(_streak); _streak.Clear();
                    return (true, FormattableString.Invariant($"the stars have been this size for {RebaselineAfter} frames: taking it as normal now (focus changed?)"));
                }
            }
            else _streak.Clear();
            return (false, why);
        }
        _streak.Clear();
        _good.Add(m);
        if (_good.Count > BaselineFrames) _good.RemoveAt(0);
        return (true, "");
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).Order().ToArray();
        if (v.Length == 0) return double.NaN;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
