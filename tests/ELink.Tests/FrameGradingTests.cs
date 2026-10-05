using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

/// <summary>Synthetic star fields: a fixed set of stars rendered as gaussians, with knobs for what goes wrong at night.</summary>
public static class SyntheticSky
{
    /// <param name="visible">share of the stars that shine through (clouds)</param>
    /// <param name="sigma">star size, pixels</param>
    /// <param name="trail">elongation along x (1 = round)</param>
    public static byte[] Frame(int w = 640, int h = 480, int stars = 60, double visible = 1, double sigma = 1.6, double trail = 1, double background = 800, int seed = 7, int frame = 0)
    {
        var rnd = new Random(seed);
        var px = new double[w * h];
        var noise = new Random(seed * 1000 + frame);
        for (int i = 0; i < px.Length; i++) px[i] = background + (noise.NextDouble() - 0.5) * 20;
        for (int s = 0; s < stars; s++)
        {
            double x = 15 + rnd.NextDouble() * (w - 30), y = 15 + rnd.NextDouble() * (h - 30), amp = 400 + rnd.NextDouble() * 4000;
            bool shows = rnd.NextDouble() < visible;
            if (!shows) continue;
            double sx = sigma * trail, sy = sigma;
            for (int yy = (int)(y - 6 * sy); yy <= (int)(y + 6 * sy); yy++)
                for (int xx = (int)(x - 6 * sx); xx <= (int)(x + 6 * sx); xx++)
                {
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    px[yy * w + xx] += amp * Math.Exp(-((xx - x) * (xx - x) / (2 * sx * sx) + (yy - y) * (yy - y) / (2 * sy * sy)));
                }
        }
        return FitsImage.Write16(w, h, px.Select(v => (ushort)Math.Clamp(Math.Round(v), 0, 65535)).ToArray());
    }

    public static FrameMetrics Measure(byte[] fits) => FrameMetrics.Measure(FitsImage.Parse(fits));
}

public class FrameGradingTests
{
    [Fact]
    public void ElongationTellsRoundFromTrailedStars()
    {
        var round = SyntheticSky.Measure(SyntheticSky.Frame());
        var trailed = SyntheticSky.Measure(SyntheticSky.Frame(trail: 3));
        Assert.InRange(round.Elongation, 1.0, 1.2);
        Assert.InRange(trailed.Elongation, 2.4, 3.6);
        Assert.InRange(round.Stars, 45, 60);
    }

    [Fact]
    public void BadFramesAreRejectedAgainstTheBaseline()
    {
        var g = new FrameGrader();
        for (int i = 0; i < 4; i++) Assert.True(g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(frame: i))).Ok);   // the baseline
        Assert.Equal(4, g.BaselineCount);

        var clouds = g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(visible: 0.2, frame: 10)));
        Assert.False(clouds.Ok); Assert.Contains("clouds", clouds.Reason);
        var trail = g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(trail: 2.5, frame: 11)));
        Assert.False(trail.Ok); Assert.Contains("trailed", trail.Reason);
        var soft = g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(sigma: 3.2, frame: 12)));
        Assert.False(soft.Ok); Assert.Contains("size", soft.Reason);
        var bright = g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(background: 4000, frame: 13)));
        Assert.False(bright.Ok); Assert.Contains("brighter", bright.Reason);
        Assert.Equal(4, g.BaselineCount);                       // rejected frames do not pull the baseline

        // slightly worse but fine: accepted (and the baseline moves with the night)
        Assert.True(g.Grade(SyntheticSky.Measure(SyntheticSky.Frame(visible: 0.8, sigma: 1.9, frame: 14))).Ok);
        Assert.False(new FrameGrader().Grade(new FrameMetrics(0, double.NaN, double.NaN, 800)).Ok);   // no stars at all, even without a baseline
        g.Reset(); Assert.Equal(0, g.BaselineCount);
    }

    [Fact]
    public void ARawColourFrameOfRoundStarsIsMeasuredRoundNotRaggedByItsColourMosaic()
    {
        // round stars seen through an RGGB mosaic: each star is sampled by pixels of different colours
        const int W = 800, H = 600;
        var rnd = new Random(4); var px = new ushort[W * H]; var noise = new Random(9);
        var stars = Enumerable.Range(0, 70).Select(_ => (X: 20 + rnd.NextDouble() * (W - 40), Y: 20 + rnd.NextDouble() * (H - 40), F: 1500 + rnd.NextDouble() * 9000)).ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double gain = (y % 2, x % 2) switch { (0, 0) => 1.0, (1, 1) => 0.55, _ => 0.85 };   // the colours see a white star differently
                double v = 600 + (noise.NextDouble() - 0.5) * 30;
                foreach (var st in stars) { double dx = x - st.X, dy = y - st.Y; if (Math.Abs(dx) < 9 && Math.Abs(dy) < 9) v += gain * st.F * Math.Exp(-(dx * dx + dy * dy) / (2 * 1.7 * 1.7)); }
                px[y * W + x] = (ushort)Math.Min(65535, v);
            }
        var img = FitsImage.Parse(FitsImage.Write16(W, H, px, new Dictionary<string, string> { ["BAYERPAT"] = "'RGGB'" }));
        var m = FrameMetrics.Measure(img);
        Assert.True(m.Stars > 20);
        Assert.True(m.Elongation < 1.25, $"measured as the cells' brightness: {m.Elongation}");
        Assert.InRange(m.Hfr, 2.0, 6.0);           // in the sensor's pixels
    }

    [Fact]
    public void AStableChangeOfStarSizeBecomesTheNewNormalAndABriefOneDoesNot()
    {
        FrameMetrics Soft(double hfr) => new(300, hfr, 1.2, 165);
        var g = new FrameGrader();
        for (int i = 0; i < 10; i++) Assert.True(g.Grade(Soft(3.1 + 0.05 * (i % 3))).Ok);
        // a passing patch: rejected, and the usual is still the usual
        for (int i = 0; i < 3; i++) Assert.False(g.Grade(Soft(7.5)).Ok);
        Assert.True(g.Grade(Soft(3.2)).Ok);
        Assert.False(g.Grade(Soft(7.4)).Ok);              // (the patch started again: the count starts again too)
        // seven frames in a row of one size: it was the focus that changed
        var results = Enumerable.Range(0, 8).Select(i => g.Grade(Soft(7.3 + 0.1 * (i % 3)))).ToList();
        Assert.True(results.Take(4).All(r => !r.Ok));
        var adopted = results.First(r => r.Ok);
        Assert.Contains("taking it as normal", adopted.Reason);
        Assert.True(g.Grade(Soft(7.4)).Ok);
        Assert.False(g.Grade(Soft(15)).Ok);               // and twice the new normal is still bad
    }

    [Fact]
    public void ACameraWhoseStarsAreAlwaysALittleLongIsJudgedAgainstItsOwnUsual()
    {
        FrameMetrics M(double elong) => new(300, 3.0, elong, 165);
        var g = new FrameGrader();
        for (int i = 0; i < 8; i++) Assert.True(g.Grade(M(1.5)).Ok);
        Assert.True(g.Grade(M(1.75)).Ok);                 // above the fixed 1.6, but near its usual
        Assert.False(g.Grade(M(2.2)).Ok);                 // really trailed
        var fresh = new FrameGrader();
        Assert.False(fresh.Grade(M(1.75)).Ok);            // with no usual yet, the fixed limit
    }
}
