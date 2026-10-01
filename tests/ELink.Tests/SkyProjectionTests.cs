using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests;

public class SkyProjectionTests
{
    [Fact]
    public void TheCentreIsInTheMiddleNorthIsUpEastIsLeft()
    {
        var p = new SkyProjection(5.5, 10, 20, 800, 600);
        Assert.True(p.TryProject(5.5, 10, out var x, out var y)); Assert.Equal(400, x, 6); Assert.Equal(300, y, 6);
        Assert.True(p.TryProject(5.5, 12, out _, out var yn)); Assert.True(yn < 300);              // north is up
        Assert.True(p.TryProject(5.6, 10, out var xe, out _)); Assert.True(xe < 400);              // east (more RA) is left
    }

    [Fact]
    public void TheFieldOfViewSpansTheWidth()
    {
        var p = new SkyProjection(0, 0, 30, 1000, 700);
        Assert.True(p.TryProject(23, 0, out var xr, out _));                                        // 15 degrees west of centre (RA 23h)
        Assert.Equal(1000, xr, 1);
        Assert.InRange(p.PixelsPerDegree, 1000 / 30.0 * 0.95, 1000 / 30.0 * 1.05);
    }

    [Theory]
    [InlineData(5.5, 10, 20)] [InlineData(0.1, 80, 60)] [InlineData(12, -89, 5)] [InlineData(23.9, -30, 1)] [InlineData(18, 45, 120)]
    public void UnprojectIsTheInverse(double ra, double dec, double fov)
    {
        var p = new SkyProjection(ra, dec, fov, 1200, 800);
        var rnd = new Random(1);
        for (int i = 0; i < 50; i++)
        {
            double x = rnd.NextDouble() * 1200, y = rnd.NextDouble() * 800;
            var (r, d) = p.Unproject(x, y);
            Assert.True(p.TryProject(r, d, out var x2, out var y2));
            Assert.Equal(x, x2, 6); Assert.Equal(y, y2, 6);
        }
    }

    [Fact]
    public void TheFarSideIsNotDrawnAndTheQueryRadiusCoversTheCorners()
    {
        var p = new SkyProjection(6, 0, 40, 800, 600);
        Assert.False(p.TryProject(18, 0, out _, out _));                                            // directly behind
        Assert.InRange(p.RadiusDegrees, 20, 30);                                                    // half the diagonal of a 40x30 chart
    }
}
