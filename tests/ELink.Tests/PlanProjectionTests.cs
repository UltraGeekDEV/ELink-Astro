using ELink.Core.Astro;
using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

/// <summary>How an image area lies on the sky, also when it is large: the one mapping (PlanProjection) that the chart, the poses, the
/// coverage and the stacked picture share.</summary>
public class PlanProjectionTests
{
    public static IEnumerable<object[]> Centres() => new[]
    {
        new object[] { 5.6, 0.0 }, new object[] { 12.0, 70.0 }, new object[] { 3.0, 88.0 }, new object[] { 20.0, -60.0 }, new object[] { 0.1, -85.0 },
    };

    [Theory, MemberData(nameof(Centres))]
    public void ToSkyAndBackAreInverseAtAnyCentreAngleAndSize(double ra, double dec)
    {
        foreach (double pa in new[] { 0.0, 37, -90, 170 })
            foreach (var (x, y) in new[] { (0.0, 0.0), (1.0, -0.5), (12.0, 7.0), (-30.0, 20.0), (45.0, -35.0), (-60.0, 5.0) })
            {
                var (r, d) = PlanProjection.ToSky(ra, dec, pa, x, y);
                Assert.True(PlanProjection.TryFromSky(ra, dec, pa, r, d, out var bx, out var by));
                Assert.Equal(x, bx, 7); Assert.Equal(y, by, 7);
            }
    }

    [Fact]
    public void ForSmallAreasItIsTheSameAsAddingAngles()
    {
        // the old way: rotate angle offsets, then project (kept here as the reference for fields of a few degrees)
        foreach (double pa in new[] { 0.0, 30, 135 })
        {
            double t = pa * Math.PI / 180;
            foreach (var (x, y) in new[] { (0.4, 0.2), (-1.0, 0.7), (1.5, -1.0) })
            {
                var (r0, d0) = Gnomonic.ToSky(5.6, 20, x * Math.Cos(t) + y * Math.Sin(t), -x * Math.Sin(t) + y * Math.Cos(t));
                var (r1, d1) = PlanProjection.ToSky(5.6, 20, pa, x, y);
                Assert.True(Sky.SeparationDegrees(r0, d0, r1, d1) * 3600 < 2, $"({x},{y}) at {pa}°: {Sky.SeparationDegrees(r0, d0, r1, d1) * 3600:0.0}\" apart");
            }
        }
    }

    [Fact]
    public void AnAreaIsExactlyAsWideAsAskedAlongItsAxesThroughTheCentre()
    {
        foreach (double pa in new[] { 0.0, 45, 110 })
            foreach (double half in new[] { 5.0, 30, 50 })
            {
                var (r, d) = PlanProjection.ToSky(8.0, 40, pa, half, 0);
                Assert.Equal(half, Sky.SeparationDegrees(8.0, 40, r, d), 7);
                var (r2, d2) = PlanProjection.ToSky(8.0, 40, pa, 0, -half);
                Assert.Equal(half, Sky.SeparationDegrees(8.0, 40, r2, d2), 7);
            }
    }

    [Theory]
    [InlineData(10, 8, 0, 20)]
    [InlineData(40, 25, 37, 0)]
    [InlineData(60, 40, 0, 70)]
    [InlineData(60, 40, 37, 70)]
    [InlineData(90, 60, 90, 45)]
    public void TheStackGridReachesExactlyTheAreasCorners(double w, double h, double pa, double dec)
    {
        // the live stack is a tangent grid of 2·tan(half) in tangent degrees: its corners must be the area's corners
        double sw = PlanProjection.TangentSpan(w), sh = PlanProjection.TangentSpan(h);
        const int px = 2000;
        double scale = sw * 3600 / px;                              // arcsec per pixel
        int py = (int)Math.Round(sh * 3600 / scale);
        var wcs = TanWcs.Centered(84.0, dec, pa, scale, px, py);
        double hy = py * scale / 3600;                              // the grid's real height after rounding to whole pixels
        var area = new[] { (-w / 2, h / 2), (w / 2, h / 2), (w / 2, -h / 2), (-w / 2, -h / 2) }.Select(c => PlanProjection.ToSky(84.0 / 15, dec, pa, c.Item1, c.Item2)).ToList();
        var grid = new[] { (-0.5, -0.5), (px - 0.5, -0.5), (px - 0.5, py - 0.5), (-0.5, py - 0.5) }.Select(c => { var s = wcs.PixelToSky(c.Item1, c.Item2); return (s.Ra / 15, s.Dec); }).ToList();
        foreach (var g in grid)
        {
            double nearest = area.Min(a => Sky.SeparationDegrees(a.RaHours, a.DecDegrees, g.Item1, g.Item2));
            Assert.True(nearest < Math.Max(0.02, 0.003 * Math.Max(w, h)) + Math.Abs(hy - sh) , $"a grid corner is {nearest:0.###}° from the nearest corner of the {w}x{h} area at PA {pa}, dec {dec}");
        }
    }

    [Fact]
    public void WithoutTheTangentSpanALargeStackWouldMissItsEdges()
    {
        // 60° wide, asked for as 60 tangent degrees: the area's edge is at tan(30°) = 33.1 tangent degrees: 3° short
        Assert.True(PlanProjection.TangentSpan(60) - 60 > 5.9);
        Assert.Equal(1, PlanProjection.TangentSpan(1), 3);              // tiny areas: no difference
        Assert.True(PlanProjection.TangentSpan(120) > PlanProjection.TangentSpan(60));
        Assert.Equal(179, PlanProjection.TangentSpan(179));             // past the tangent plane's reach it just says so
    }
}
