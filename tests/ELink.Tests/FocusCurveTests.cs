using ELink.Automation;
using Xunit;

namespace ELink.Tests;

public class FocusCurveTests
{
    private static (double, double)[] Hyperbola(double best, double a, double b, params double[] xs) =>
        xs.Select(x => (x, Math.Sqrt(a * a + b * b * (x - best) * (x - best)))).ToArray();

    [Fact]
    public void FindsTheVertexOfAHyperbola()
    {
        var pts = Hyperbola(36700, 1.7, 0.0002, 27000, 30000, 33000, 36000, 39000, 42000, 45000);
        var fit = FocusCurve.Parabola(pts);
        Assert.True(fit.Valid);
        Assert.InRange(fit.BestPosition, 36650, 36750);
        Assert.Equal(1.7, fit.BestHfr, 0.05);
        Assert.True(fit.RSquared > 0.999);
    }

    [Fact]
    public void ExtrapolatesWhenTheMinimumIsOutsideTheSamples()
    {
        var fit = FocusCurve.Parabola(Hyperbola(36700, 1.7, 0.0002, 46000, 49000, 52000, 55000, 58000));
        Assert.True(fit.Valid);
        Assert.InRange(fit.BestPosition, 33000, 40000);
    }

    [Fact]
    public void ToleratesNoiseAndMissingStars()
    {
        var rnd = new Random(3);
        var pts = Hyperbola(20000, 2.0, 0.0003, 14000, 16000, 18000, 20000, 22000, 24000, 26000)
            .Select(p => (p.Item1, p.Item2 + (rnd.NextDouble() - 0.5) * 0.1)).ToList();
        pts[0] = (pts[0].Item1, double.NaN);
        var fit = FocusCurve.Parabola(pts);
        Assert.True(fit.Valid);
        Assert.InRange(fit.BestPosition, 19400, 20600);
    }

    [Fact]
    public void RejectsAStraightSlopeAndTooFewPoints()
    {
        // a slope fits a parabola whose vertex is far outside the samples: the caller shifts its window
        var slope = FocusCurve.Parabola(new[] { (1000.0, 2.0), (2000.0, 3.0), (3000.0, 4.0), (4000.0, 5.0) });
        Assert.True(!slope.Valid || slope.BestPosition < 1000);
        // a hill (worst focus in the middle) has no minimum at all
        Assert.False(FocusCurve.Parabola(new[] { (1000.0, 2.0), (2000.0, 4.0), (3000.0, 5.0), (4000.0, 4.0), (5000.0, 2.0) }).Valid);
        Assert.False(FocusCurve.Parabola(new[] { (1000.0, 2.0), (2000.0, 3.0) }).Valid);
    }
}
