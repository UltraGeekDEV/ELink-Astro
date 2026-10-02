using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class LiveStackerTests
{
    private const double RA = 83.8, DEC = -5.4;

    [Fact]
    public void CenteredGridPointsUpAtThePositionAngleWithEastLeft()
    {
        var w = TanWcs.Centered(RA, DEC, 0, 10, 101, 101);
        var (ra0, dec0) = w.PixelToSky(50, 50);
        Assert.Equal(RA, ra0, 6); Assert.Equal(DEC, dec0, 6);
        var (_, decUp) = w.PixelToSky(50, 60);
        Assert.Equal(DEC + 100 / 3600.0, decUp, 5);                       // +y is north
        var (raRight, _) = w.PixelToSky(60, 50);
        Assert.True(raRight < RA);                                        // +x is west
        var turned = TanWcs.Centered(RA, DEC, 90, 10, 101, 101);
        Assert.True(turned.PixelToSky(50, 60).Ra > RA);                   // PA 90: up is east
        Assert.Equal(10, turned.PixelScaleArcsec, 9);
    }

    [Fact]
    public void PixelSkyRoundTripAndHomographyAgreeWithTheProjections()
    {
        var a = TanWcs.Centered(RA, DEC, 23, 3.1, 800, 600);
        var b = TanWcs.Centered(RA + 0.6, DEC - 0.4, -71, 1.7, 1500, 1200) with { CrPix1 = 700.3, CrPix2 = 610.8 };
        var h = a.HomographyTo(b);
        foreach (var (x, y) in new[] { (0.0, 0.0), (799.0, 0.0), (400.5, 300.25), (13.0, 590.0) })
        {
            var (ra, dec) = a.PixelToSky(x, y);
            var (bx, by) = b.SkyToPixel(ra, dec);
            double z = h[6] * x + h[7] * y + h[8];
            Assert.Equal(bx, (h[0] * x + h[1] * y + h[2]) / z, 6);
            Assert.Equal(by, (h[3] * x + h[4] * y + h[5]) / z, 6);
            var (rx, ry) = a.SkyToPixel(ra, dec);
            Assert.Equal(x, rx, 6); Assert.Equal(y, ry, 6);
        }
    }

    [Fact]
    public void WcsSurvivesAFitsRoundTrip()
    {
        var w = TanWcs.Centered(RA, DEC, 37, 2.5, 64, 48);
        var fits = FitsImage.WriteFloat32(64, 48, new float[64 * 48], w.Cards());
        var back = TanWcs.FromHeader(FitsImage.Parse(fits).Header)!;
        Assert.Equal(w.CrVal1, back.CrVal1, 9); Assert.Equal(w.CrPix2, back.CrPix2, 9);
        Assert.Equal(w.Cd12, back.Cd12, 12); Assert.Equal(w.Cd21, back.Cd21, 12);
    }

    // a smooth sky: broad gaussian "nebulae" over a gradient, in surface brightness, so a correct resampler reproduces it
    private static double Sky(double ra, double dec)
    {
        double dx = (ra - RA) * Math.Cos(DEC * Math.PI / 180) * 3600, dy = (dec - DEC) * 3600;   // arcsec
        double g(double cx, double cy, double s) => Math.Exp(-((dx - cx) * (dx - cx) + (dy - cy) * (dy - cy)) / (2 * s * s));
        return 100 + 0.01 * dx + 1000 * g(0, 0, 60) + 600 * g(-300, 200, 45) + 400 * g(250, -150, 80);
    }

    private static float[] Render(TanWcs w, int width, int height, Func<double, double, double> f)
    {
        var d = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { var (ra, dec) = w.PixelToSky(x, y); d[y * width + x] = (float)f(ra, dec); }
        return d;
    }

    private static (double Rms, int N) Compare(LiveStacker s, Func<double, double, double> f, int margin = 3)
    {
        var mean = s.Mean();
        double e = 0; int n = 0;
        for (int y = margin; y < s.Height - margin; y++)
            for (int x = margin; x < s.Width - margin; x++)
            {
                float m = mean[y * s.Width + x];
                if (float.IsNaN(m)) continue;
                // skip the frame edge: partial pixels there mix with nothing
                if (float.IsNaN(mean[(y - margin) * s.Width + x]) || float.IsNaN(mean[(y + margin) * s.Width + x]) ||
                    float.IsNaN(mean[y * s.Width + x - margin]) || float.IsNaN(mean[y * s.Width + x + margin])) continue;
                var (ra, dec) = s.Wcs.PixelToSky(x, y);
                e += Math.Pow(m - f(ra, dec), 2); n++;
            }
        return (Math.Sqrt(e / Math.Max(1, n)), n);
    }

    [Theory]
    [InlineData(4.0, 1.3, 1)]    // output 3x finer: interpolated
    [InlineData(4.0, 4.0, 1)]    // same scale, rotated
    [InlineData(4.0, 6.5, 1)]    // 1.6x coarser: area-sampled
    [InlineData(4.0, 15.0, 3)]   // 3.75x coarser: binned 3x, then area-sampled
    public void RotatedFramesLandOnTheSkyAtAnyOutputScale(double inScale, double outScale, int expectBin)
    {
        int fw = 400, fh = 300;
        double outSpanArcsec = 1400;
        int ow = (int)(outSpanArcsec / outScale), oh = (int)(outSpanArcsec / outScale);
        var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 10, outScale, ow, oh), ow, oh);
        var frames = new[]
        {
            TanWcs.Centered(RA, DEC, 0, inScale, fw, fh),
            TanWcs.Centered(RA + 0.05, DEC + 0.03, 33, inScale, fw, fh),
            TanWcs.Centered(RA - 0.04, DEC - 0.02, -120, inScale, fw, fh),
        };
        foreach (var fw0 in frames)
        {
            var r = stack.Add(Render(fw0, fw, fh, Sky), fw, fh, fw0);
            Assert.True(r.Added, r.Message);
            Assert.Equal(expectBin, r.BinFactor);
        }
        Assert.Equal(3, stack.Frames);
        var (rms, n) = Compare(stack, Sky);
        Assert.True(n > 1000, $"{n} pixels compared");
        Assert.True(rms < 4, $"rms {rms:0.00} against a sky peaking at ~1100");   // well below 0.5 %
    }

    [Fact]
    public void DownscalingAveragesInsteadOfAliasing()
    {
        // a pixel-level checkerboard: decimating it gives 0 or 1 at random, averaging gives 0.5 everywhere
        int fw = 300, fh = 300;
        var fwcs = TanWcs.Centered(RA, DEC, 0, 2, fw, fh);
        var board = new float[fw * fh];
        for (int y = 0; y < fh; y++) for (int x = 0; x < fw; x++) board[y * fw + x] = (x + y) % 2;
        foreach (var outScale in new[] { 5.3, 7.0, 9.7 })
        {
            int n = (int)(500 / outScale);
            var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 17, outScale, n, n), n, n);
            Assert.True(stack.Add(board, fw, fh, fwcs).Added);
            var m = stack.Mean();
            int c = n / 2;
            for (int y = c - 5; y <= c + 5; y++)
                for (int x = c - 5; x <= c + 5; x++)
                    Assert.InRange(m[y * n + x], 0.4f, 0.6f);
        }
    }

    [Fact]
    public void UpsamplingInterpolatesAStarToTheRightPlace()
    {
        // a sharp star (sigma 1.2 input pixels) at a known sky position; the stack at 4x finer scale puts its centroid there
        double sRa = RA + 0.01, sDec = DEC - 0.007;
        double Star(double ra, double dec)
        {
            double dx = (ra - sRa) * Math.Cos(DEC * Math.PI / 180) * 3600, dy = (dec - sDec) * 3600;
            return 10 + 5000 * Math.Exp(-(dx * dx + dy * dy) / (2 * 6.0 * 6.0));   // input scale 5"/px: sigma 1.2 px
        }
        int fw = 200, fh = 160;
        var fwcs = TanWcs.Centered(RA, DEC, 41, 5, fw, fh);
        int n = 600;
        var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 0, 1.25, n, n), n, n);
        Assert.True(stack.Add(Render(fwcs, fw, fh, Star), fw, fh, fwcs, background: 10).Added);
        var m = stack.Mean();
        var (ex, ey) = stack.Wcs.SkyToPixel(sRa, sDec);
        double sx = 0, sy = 0, sw = 0;
        for (int y = (int)ey - 30; y <= (int)ey + 30; y++)
            for (int x = (int)ex - 30; x <= (int)ex + 30; x++)
            {
                double v = Math.Max(0, m[y * n + x]);
                sx += v * x; sy += v * y; sw += v;
            }
        Assert.Equal(ex, sx / sw, 0.3); Assert.Equal(ey, sy / sw, 0.3);   // within 0.3 output px = 0.4"
        Assert.InRange(m[(int)Math.Round(ey) * n + (int)Math.Round(ex)], 4500, 5300);  // the peak survives
    }

    [Fact]
    public void OverlappingFramesAverageAndCoverageGrows()
    {
        int fw = 100, fh = 100;
        var a = TanWcs.Centered(RA, DEC, 0, 4, fw, fh);
        var b = TanWcs.Centered(RA + 200 / 3600.0 / Math.Cos(DEC * Math.PI / 180), DEC, 0, 4, fw, fh);  // 200" east
        var stack = new LiveStacker(TanWcs.Centered(RA + 100 / 3600.0, DEC, 0, 4, 200, 100), 200, 100);
        stack.Add(Enumerable.Repeat(10f, fw * fh).ToArray(), fw, fh, a);
        double one = stack.Coverage();
        stack.Add(Enumerable.Repeat(30f, fw * fh).ToArray(), fw, fh, b);
        Assert.True(stack.Coverage() > one * 1.4);
        var m = stack.Mean();
        var (ox, oy) = stack.Wcs.SkyToPixel(RA + 100 / 3600.0 / Math.Cos(DEC * Math.PI / 180), DEC);   // in both frames
        Assert.Equal(20.0, m[(int)oy * 200 + (int)ox], 2);
        var reduced = stack.Reduced(50, 50);
        Assert.Equal(50, reduced.Width); Assert.Equal(25, reduced.Height);
        Assert.Equal(16, reduced.Wcs.PixelScaleArcsec, 6);
    }

    private static float[] Scene(TanWcs w, int width, int height, double gain, double sky, bool streak)
    {
        var d = Render(w, width, height, (ra, dec) => sky + gain * (Sky(ra, dec) - 100));
        if (streak) for (int x = 0; x < width; x++) for (int dy = -1; dy <= 1; dy++) d[(height / 2 + dy) * width + x] += 30000;   // a satellite
        return d;
    }

    [Fact]
    public void ASatelliteTrailInOneFrameIsRejected()
    {
        int fw = 200, fh = 150;
        var fwcs = TanWcs.Centered(RA, DEC, 0, 4, fw, fh);
        foreach (bool reject in new[] { false, true })
        {
            var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 0, 4, fw, fh), fw, fh) { RejectSigma = reject ? 3 : 0 };
            for (int n = 0; n < 11; n++)
            {
                var r = stack.Add(Scene(fwcs, fw, fh, 1, 500, streak: n == 6), fw, fh, 1, fwcs, [500f]);
                if (reject && n == 6) Assert.True(r.RejectedPixels >= fw * 3 * 0.9, $"{r.RejectedPixels} pixels left out");
            }
            var m = stack.Mean();
            var (ra, dec) = stack.Wcs.PixelToSky(100, 75);
            double truth = Sky(ra, dec) - 100;
            if (reject) Assert.Equal(truth, m[75 * fw + 100], 0);                     // the trail is gone
            else Assert.True(m[75 * fw + 100] > truth + 2000, "without rejection the trail shows (one frame in eleven)");
        }
    }

    [Fact]
    public void FramesFromAnotherCameraAreScaledToMatchTheStack()
    {
        int fw = 200, fh = 150;
        var a = TanWcs.Centered(RA, DEC, 0, 4, fw, fh);
        var b = TanWcs.Centered(RA + 0.02, DEC, 20, 4, fw, fh);
        var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 0, 4, fw, fh), fw, fh) { MatchFlux = true };
        Assert.Equal(1.0, stack.Add(Scene(a, fw, fh, 1, 500, false), fw, fh, 1, a, [500f]).FluxScale);
        // the second camera records half the signal on a different sky level
        var r = stack.Add(Scene(b, fw, fh, 0.5, 1200, false), fw, fh, 1, b, [1200f]);
        Assert.Equal(2.0, r.FluxScale, 1);
        var m = stack.Mean();
        var (ra, dec) = stack.Wcs.PixelToSky(100, 75);
        Assert.InRange(m[75 * fw + 100], Sky(ra, dec) - 100 - 10, Sky(ra, dec) - 100 + 10);   // within a few ADU of the first camera's level
    }

    [Fact]
    public void AStackSurvivesSavingAndLoading()
    {
        int fw = 120, fh = 90;
        var a = TanWcs.Centered(RA, DEC, 0, 4, fw, fh);
        var stack = new LiveStacker(TanWcs.Centered(RA, DEC, 10, 5, 100, 80), 100, 80) { RejectSigma = 3 };
        stack.Add(Scene(a, fw, fh, 1, 500, false), fw, fh, 1, a, [500f]);
        stack.Add(Scene(a, fw, fh, 1, 600, false), fw, fh, 1, a, [600f]);
        using var ms = new MemoryStream();
        stack.WriteTo(ms);
        ms.Position = 0;
        var back = LiveStacker.ReadFrom(ms);
        Assert.Equal(2, back.Frames); Assert.Equal(stack.Width, back.Width); Assert.Equal(stack.Wcs, back.Wcs);
        Assert.Equal(stack.Mean(), back.Mean());
        Assert.Throws<FormatException>(() => LiveStacker.ReadFrom(new MemoryStream(new byte[] { 3, 65, 66, 67 })));
    }
}
