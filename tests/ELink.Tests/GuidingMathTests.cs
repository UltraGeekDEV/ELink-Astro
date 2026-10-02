using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class GuidingMathTests
{
    private static Star S(double x, double y, double flux = 1000, double peak = 5000, double hfr = 2) => new(x, y, flux, hfr, peak);

    private static List<Star> Field(double dx = 0, double dy = 0) =>
    [
        S(100 + dx, 100 + dy, 9000), S(300 + dx, 120 + dy, 8000), S(500 + dx, 400 + dy, 7000), S(220 + dx, 330 + dy, 6000),
        S(610 + dx, 90 + dy, 5000), S(80 + dx, 450 + dy, 4000),
    ];

    [Fact]
    public void LocksOnGoodStarsAndMeasuresTheShiftRobustly()
    {
        var stars = Field();
        stars.Add(S(5, 5, 99999));                 // at the edge
        stars.Add(S(400, 200, 99999, peak: 65535)); // saturated
        stars.Add(S(250, 250, 9500)); stars.Add(S(255, 252, 100));   // a close pair: neither is isolated
        var t = StarTracker.Acquire(stars, 700, 500, 60000)!;
        Assert.Equal(6, t.Lock.Count);
        Assert.Equal((100.0, 100.0), t.Lock[0]);

        var moved = Field(3.2, -1.7);
        moved.RemoveAt(2);                              // a star lost to a cloud
        moved[0] = S(140, 140, 9000);                   // ... and one replaced by junk far away
        moved.Add(S(301.0, 120.0));                     // a newcomer near a lock star's old place, not where it went
        var m = t.Measure(moved, 3, -2, 12)!.Value;
        Assert.Equal(3.2, m.Dx, 6); Assert.Equal(-1.7, m.Dy, 6);
        Assert.Equal(4, m.Matched);
        Assert.Null(t.Measure([S(10, 10)], 0, 0));
        Assert.Null(StarTracker.Acquire([S(2, 2)], 700, 500, 60000));
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(37.0, false)]
    [InlineData(-120.0, true)]
    public void CalibrationTurnsErrorsIntoPulsesAndSkyComponents(double angle, bool mirrored)
    {
        // a West pulse moves the stars 2.5 px/s along the camera's "east", a North pulse 3 px/s along its "south"
        double a = angle * Math.PI / 180, flip = mirrored ? -1 : 1;
        (double X, double Y) east = (Math.Cos(a), Math.Sin(a)), south = (-Math.Sin(a) * flip, Math.Cos(a) * flip);
        var cal = new PulseCalibration(east.X * 0.0025, east.Y * 0.0025, south.X * 0.003, south.Y * 0.003, DecDegrees: 20);
        Assert.Equal(90, cal.AxisAngleDegrees, 6);

        // stars moved 4 px "east" and 1.5 px "south" (the pointing drifted west and north)
        double ex = 4 * east.X + 1.5 * south.X, ey = 4 * east.Y + 1.5 * south.Y;
        var (ce, cs) = cal.Components(ex, ey);
        Assert.Equal(4, ce, 9); Assert.Equal(1.5, cs, 9);
        // to bring them back: East (negative West) and South (negative North) pulses
        var (raMs, decMs) = cal.PulsesFor(-ex, -ey, 20);
        Assert.Equal(-1600, raMs, 6); Assert.Equal(-500, decMs, 6);
        // further from the pole the RA axis moves the stars faster on the sky: shorter pulses at Dec 0 than at Dec 20
        var (raAt0, _) = cal.PulsesFor(-ex, -ey, 0);
        Assert.Equal(-1600 * Math.Cos(20 * Math.PI / 180), raAt0, 6);
    }
}
