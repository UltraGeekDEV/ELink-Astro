using ELink.Imaging;
using Xunit;

namespace ELink.Tests;

public class ImagingTests
{
    [Fact]
    public void RoundTrips16BitFits()
    {
        var px = new ushort[6 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)(i * 1000);
        var img = FitsImage.Parse(FitsImage.Write16(6, 4, px, new Dictionary<string, string> { ["EXPTIME"] = "30.5", ["FILTER"] = "'Blue'" }));
        Assert.Equal(6, img.Width); Assert.Equal(4, img.Height); Assert.Equal(1, img.Channels);
        Assert.Equal(65535, img.Range);
        for (int i = 0; i < px.Length; i++) Assert.Equal(px[i], img.Data[i]);
        Assert.Equal(30.5, img.GetDouble("EXPTIME"));
        Assert.Equal("Blue", img.Get("FILTER"));
    }

    [Fact]
    public void RejectsTruncatedAndGarbage()
    {
        var good = FitsImage.Write16(4, 4, new ushort[16]);
        Assert.Throws<FormatException>(() => FitsImage.Parse(good.AsSpan(0, good.Length - 3000)));
        Assert.Throws<FormatException>(() => FitsImage.Parse(new byte[100]));
    }

    [Fact]
    public void AutoStretchLiftsADimBackgroundAndKeepsStars()
    {
        var rnd = new Random(1);
        var px = new ushort[200 * 200];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)(1000 + rnd.Next(-30, 30));   // dim sky
        px[100 * 200 + 100] = 60000;                                                      // a star
        var img = FitsImage.Parse(FitsImage.Write16(200, 200, px));
        var bgra = AutoStretch.ToBgra(img);
        Assert.Equal(200 * 200 * 4, bgra.Length);
        // a pixel from the background is lifted to roughly the target grey (0.25 -> ~64), the star is near white
        byte sky = bgra[(50 * 200 + 50) * 4];
        Assert.InRange(sky, 40, 90);
        // FITS row 100 from the bottom is image row 99 from the top
        Assert.True(bgra[(99 * 200 + 100) * 4] > 240);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void MtfFixedPoints()
    {
        Assert.Equal(0, AutoStretch.Mtf(0.3, 0), 9);
        Assert.Equal(1, AutoStretch.Mtf(0.3, 1), 9);
        Assert.Equal(0.5, AutoStretch.Mtf(0.3, 0.3), 9);
    }
}
