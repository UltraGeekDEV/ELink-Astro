using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class DebayerTests
{
    // a colour scene with a different linear ramp per channel: bilinear demosaicing reconstructs linear data exactly
    private static double R(double x, double y) => 1000 + 7 * x + 3 * y;
    private static double G(double x, double y) => 500 - 2 * x + 5 * y;
    private static double B(double x, double y) => 200 + 4 * x - 1 * y;
    private static double Channel(int c, double x, double y) => c switch { 0 => R(x, y), 1 => G(x, y), _ => B(x, y) };

    /// <summary>What a colour camera with this filter pattern records: one channel per pixel.</summary>
    private static float[] Mosaic(int w, int h, string pattern, Func<int, double, double, double> scene)
    {
        var raw = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = pattern[(y % 2) * 2 + x % 2] switch { 'R' => 0, 'G' => 1, _ => 2 };
                raw[y * w + x] = (float)scene(c, x, y);
            }
        return raw;
    }

    [Fact]
    public void PatternComesFromTheHeaderWithItsOffsets()
    {
        FitsImage Img(params (string, string)[] cards) =>
            FitsImage.Parse(FitsImage.Write16(4, 4, new ushort[16], cards.ToDictionary(c => c.Item1, c => c.Item2)));
        Assert.Null(Debayer.PatternOf(Img()));
        Assert.Equal("RGGB", Debayer.PatternOf(Img(("BAYERPAT", "'RGGB    '"))));
        Assert.Equal("GRBG", Debayer.PatternOf(Img(("BAYERPAT", "'RGGB'"), ("XBAYROFF", "1"))));
        Assert.Equal("GBRG", Debayer.PatternOf(Img(("BAYERPAT", "'RGGB'"), ("YBAYROFF", "1"))));
        Assert.Equal("BGGR", Debayer.PatternOf(Img(("BAYERPAT", "'RGGB'"), ("XBAYROFF", "1"), ("YBAYROFF", "1"))));
        Assert.Equal("RGGB", Debayer.Shift("BGGR", 1, 1));
        Assert.Null(Debayer.Normalize("XYZW"));
        Assert.Equal("GBRG", Debayer.Normalize(" gbrg "));
    }

    [Theory]
    [InlineData("RGGB")] [InlineData("BGGR")] [InlineData("GRBG")] [InlineData("GBRG")]
    public void InterpolatedReconstructsEveryChannelAtFullResolution(string pattern)
    {
        int w = 40, h = 30;
        var c = Debayer.Bilinear(Mosaic(w, h, pattern, Channel), w, h, pattern);
        Assert.Equal((w, h, 1), (c.Width, c.Height, c.Bin));
        for (int ch = 0; ch < 3; ch++)
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    Assert.Equal(Channel(ch, x, y), c.Data[ch * w * h + y * w + x], 3);
    }

    [Theory]
    [InlineData("RGGB")] [InlineData("BGGR")] [InlineData("GRBG")] [InlineData("GBRG")]
    public void SuperPixelMakesOneRgbPixelPerCell(string pattern)
    {
        int w = 40, h = 30;
        var c = Debayer.SuperPixel(Mosaic(w, h, pattern, Channel), w, h, pattern);
        Assert.Equal((20, 15, 2), (c.Width, c.Height, c.Bin));
        int plane = 20 * 15;
        for (int y = 0; y < 15; y++)
            for (int x = 0; x < 20; x++)
            {
                // R and B are the one pixel of that colour in the cell, G the mean of the two
                (int X, int Y) Where(char col) { int k = pattern.IndexOf(col); return (2 * x + k % 2, 2 * y + k / 2); }
                var (rx, ry) = Where('R'); var (bx, by) = Where('B');
                int g1 = pattern.IndexOf('G'), g2 = pattern.LastIndexOf('G');
                double g = (G(2 * x + g1 % 2, 2 * y + g1 / 2) + G(2 * x + g2 % 2, 2 * y + g2 / 2)) / 2;
                Assert.Equal(R(rx, ry), c.Data[y * 20 + x], 3);
                Assert.Equal(g, c.Data[plane + y * 20 + x], 3);
                Assert.Equal(B(bx, by), c.Data[2 * plane + y * 20 + x], 3);
            }
    }

    [Fact]
    public void AFlatColourStaysFlatToTheEdgesInBothModes()
    {
        int w = 17, h = 13;   // odd sizes: super pixel drops the last row and column
        double Flat(int c, double x, double y) => c switch { 0 => 300, 1 => 120, _ => 40 };
        foreach (var p in Debayer.Patterns)
        {
            var raw = Mosaic(w, h, p, Flat);
            foreach (var mode in new[] { DebayerMode.Interpolated, DebayerMode.SuperPixel })
            {
                var c = Debayer.Apply(raw, w, h, p, mode);
                int plane = c.Width * c.Height;
                for (int ch = 0; ch < 3; ch++)
                    for (int i = 0; i < plane; i++) Assert.Equal(Flat(ch, 0, 0), c.Data[ch * plane + i], 3);
            }
        }
    }

    [Fact]
    public void RawColourFramesAreShownInColour()
    {
        int w = 8, h = 6;
        var raw = Mosaic(w, h, "RGGB", (c, _, _) => c switch { 0 => 3000, 1 => 2000, _ => 1000 });
        var fits = FitsImage.Write16(w, h, raw.Select(v => (ushort)v).ToArray(), new Dictionary<string, string> { ["BAYERPAT"] = "'RGGB'" });
        var shown = Debayer.ForDisplay(FitsImage.Parse(fits));
        Assert.Equal((3, 4, 3), (shown.Channels, shown.Width, shown.Height));
        Assert.Equal(3000.0, shown.Data[0], 1); Assert.Equal(2000.0, shown.Data[12], 1); Assert.Equal(1000.0, shown.Data[24], 1);
        var mono = FitsImage.Parse(FitsImage.Write16(w, h, new ushort[w * h]));
        Assert.Same(mono, Debayer.ForDisplay(mono));
    }

    [Fact]
    public void ColourStacksKeepTheirChannelsAndTakeMonoFramesAsGrey()
    {
        var wcs = TanWcs.Centered(10, 20, 0, 4, 50, 40);
        var stack = new LiveStacker(wcs, 50, 40);
        var rgb = new float[50 * 40 * 3];
        for (int i = 0; i < 2000; i++) { rgb[i] = 90; rgb[2000 + i] = 60; rgb[4000 + i] = 30; }
        Assert.True(stack.Add(rgb, 50, 40, 3, wcs, [0, 0, 0]).Added);
        Assert.Equal(3, stack.Channels);
        Assert.True(stack.Add(Enumerable.Repeat(60f, 2000).ToArray(), 50, 40, wcs).Added);
        var m = stack.Mean();
        Assert.Equal(6000, m.Length);
        int mid = 20 * 50 + 25;
        Assert.Equal(75.0, m[mid], 3); Assert.Equal(60.0, m[2000 + mid], 3); Assert.Equal(45.0, m[4000 + mid], 3);
        var (small, sw, sh, _) = stack.Reduced(25, 20);
        Assert.Equal(3 * sw * sh, small.Length);

        // a colour frame into a mono stack is its luminance
        var mono = new LiveStacker(wcs, 50, 40);
        Assert.True(mono.Add(Enumerable.Repeat(10f, 2000).ToArray(), 50, 40, wcs).Added);
        Assert.True(mono.Add(rgb, 50, 40, 3, wcs, [0, 0, 0]).Added);
        Assert.Equal(1, mono.Channels);
        Assert.Equal((10 + 60) / 2.0, mono.Mean()[mid], 3);

        var bin = LiveStacker.Bin(rgb, 50, 40, wcs, 2, 3);
        Assert.Equal(25 * 20 * 3, bin.Data.Length);
        Assert.Equal(30.0, bin.Data[2 * 500 + 7], 3);
    }
}
