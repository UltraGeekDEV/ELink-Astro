using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class StarFieldTests
{
    /// <summary>A synthetic sky: noisy background plus Gaussian stars of a given sigma.</summary>
    public static FitsImage Sky(double sigma, int seed = 5, int stars = 12, int size = 400, double amp = 20000)
    {
        var rnd = new Random(seed);
        var px = new double[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = 1000 + (rnd.NextDouble() - 0.5) * 60;
        for (int s = 0; s < stars; s++)
        {
            double cx = 30 + rnd.NextDouble() * (size - 60), cy = 30 + rnd.NextDouble() * (size - 60), a = amp * (0.4 + rnd.NextDouble() * 0.6);
            int r = (int)(sigma * 5) + 2;
            for (int y = (int)cy - r; y <= (int)cy + r; y++)
                for (int x = (int)cx - r; x <= (int)cx + r; x++)
                    px[y * size + x] += a * Math.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (2 * sigma * sigma));
        }
        var u = px.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
        return FitsImage.Parse(FitsImage.Write16(size, size, u));
    }

    [Theory]
    [InlineData(1.5)] [InlineData(2.5)] [InlineData(4.0)]
    public void MeasuresHfrOfGaussianStars(double sigma)
    {
        var r = StarField.Detect(Sky(sigma));
        Assert.InRange(r.Count, 8, 14);
        // for a 2D Gaussian the half-flux radius is 1.1774 sigma
        Assert.Equal(1.1774 * sigma, r.MedianHfr, 0.25 * sigma);
    }

    [Fact]
    public void HfrGrowsAsFocusWorsens()
    {
        double sharp = StarField.Detect(Sky(1.5)).MedianHfr, soft = StarField.Detect(Sky(3.5)).MedianHfr;
        Assert.True(soft > sharp * 1.8);
    }

    [Fact]
    public void FindsNothingInEmptySkyAndIgnoresHotPixels()
    {
        var rnd = new Random(1);
        var px = new ushort[200 * 200];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)(1000 + rnd.Next(-30, 30));
        px[100 * 200 + 100] = 60000;      // a hot pixel
        var r = StarField.Detect(FitsImage.Parse(FitsImage.Write16(200, 200, px)));
        Assert.Equal(0, r.Count);
        Assert.True(double.IsNaN(r.MedianHfr));
    }

    [Fact]
    public void SkipsSaturatedStars()
    {
        var img = Sky(2.5, stars: 6, amp: 90000);   // clipped to 65535 in the core
        Assert.True(StarField.Detect(img).Count < 6);
    }
}
