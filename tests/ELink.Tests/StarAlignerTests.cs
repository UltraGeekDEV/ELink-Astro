using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class StarAlignerTests
{
    private static List<Star> Field(int seed, int n = 120)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => new Star(rnd.NextDouble() * 6000, rnd.NextDouble() * 4000, Math.Pow(rnd.NextDouble(), 3) * 9000 + 200, 2, 1000)).ToList();
    }

    /// <summary>The same sky seen after a shift and a turn: the frame's positions that would land on the reference's.</summary>
    private static List<Star> Moved(List<Star> sky, double tx, double ty, double deg, Random rnd, double jitter = 0.3, int lost = 15, int extra = 15)
    {
        double c = Math.Cos(-deg * Math.PI / 180), s = Math.Sin(-deg * Math.PI / 180);   // the inverse of the turn the transform will have to find
        var list = new List<Star>();
        foreach (var st in sky.Skip(lost))
        {
            double x = st.X - tx, y = st.Y - ty;
            list.Add(st with { X = c * x - s * y + (rnd.NextDouble() - 0.5) * jitter, Y = s * x + c * y + (rnd.NextDouble() - 0.5) * jitter });
        }
        for (int i = 0; i < extra; i++) list.Add(new Star(rnd.NextDouble() * 6000, rnd.NextDouble() * 4000, 500 + rnd.NextDouble() * 3000, 2, 800));   // hot pixels, satellites
        return list;
    }

    [Theory]
    [InlineData(0, 0, 0)] [InlineData(37.4, -12.8, 0)] [InlineData(-210.5, 340, 0.05)] [InlineData(95, 60, -0.3)] [InlineData(-500, 480, 0.15)]
    public void FindsTheShiftAndTurnOfAFrame(double tx, double ty, double deg)
    {
        var sky = Field(5); var rnd = new Random(11);
        var frame = Moved(sky, tx, ty, deg, rnd);
        var t = StarAligner.Align(sky, frame);
        Assert.NotNull(t);
        Assert.True(t!.Value.Matches >= 60, $"{t.Value.Matches}");
        Assert.True(t.Value.RmsPixels < 0.6, $"{t.Value.RmsPixels}");
        // a star of the frame lands where the sky had it
        var s0 = sky[40]; var f0 = frame[40 - 15];
        var (x, y) = t.Value.Apply(f0.X, f0.Y);
        Assert.InRange(Math.Abs(x - s0.X), 0, 1.0); Assert.InRange(Math.Abs(y - s0.Y), 0, 1.0);
        Assert.Equal(deg, t.Value.RotationDegrees, 0.05);
    }

    [Fact]
    public void AnotherFieldDoesNotMatchAndTooFewStarsDoNotEither()
    {
        Assert.Null(StarAligner.Align(Field(1), Field(2)));
        Assert.Null(StarAligner.Align(Field(1, 5), Field(1, 5)));
        Assert.Null(StarAligner.Align(Field(1), Moved(Field(1), 900, 0, 0, new Random(2)), maxShift: 300));   // further than it is allowed to look
    }
}
