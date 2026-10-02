namespace ELink.Imaging;

/// <summary>What a frame looks like: how many stars, how big, how round, how bright the sky.</summary>
public sealed record FrameMetrics(int Stars, double Hfr, double Elongation, double Background)
{
    public static FrameMetrics Measure(FitsImage img)
    {
        var r = StarField.Detect(img, 6, 300);
        return new FrameMetrics(r.Count, r.MedianHfr, r.MedianElongation, r.Background);
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

    private readonly List<FrameMetrics> _good = new();

    public int BaselineCount => _good.Count;

    /// <summary>Forget the baseline (a new part of the sky: other stars, another sky brightness).</summary>
    public void Reset() => _good.Clear();

    public (bool Ok, string Reason) Grade(FrameMetrics m)
    {
        if (m.Stars == 0) return (false, "no stars (clouds? the dome? the cap?)");
        if (!double.IsNaN(m.Elongation) && m.Elongation > MaxElongation) return (false, FormattableString.Invariant($"stars trailed (elongation {m.Elongation:0.00})"));
        if (_good.Count >= 3)
        {
            double stars = Median(_good.Select(g => (double)g.Stars)), hfr = Median(_good.Select(g => g.Hfr)), bg = Median(_good.Select(g => g.Background));
            // size and sky first: soft or washed-out frames also lose stars, and the cause is the more useful answer
            if (!double.IsNaN(hfr) && m.Hfr > MaxHfrRatio * hfr) return (false, FormattableString.Invariant($"stars {m.Hfr / hfr:0.0}x their usual size (focus, seeing?)"));
            if (bg > 0 && m.Background > MaxBackgroundRatio * bg) return (false, FormattableString.Invariant($"sky {m.Background / bg:0.0}x brighter than usual"));
            if (m.Stars < MinStarsFraction * stars) return (false, FormattableString.Invariant($"{m.Stars} stars where {stars:0} are usual (clouds?)"));
        }
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
