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
    public void AutoStretchShowsAVeryDimFrame()
    {
        // like a 1 s simulator exposure: everything within the lowest ten levels of a 16-bit range
        var rnd = new Random(2);
        var px = new ushort[100 * 100];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)rnd.Next(0, 10);
        var bgra = AutoStretch.ToBgra(FitsImage.Parse(FitsImage.Write16(100, 100, px)));
        int lit = 0; for (int i = 0; i < px.Length; i++) if (bgra[i * 4] > 20) lit++;
        Assert.True(lit > px.Length / 2, "a dim but varying frame must not render black");
    }

    [Fact]
    public void TopDownRowOrderIsHonoured()
    {
        var px = new ushort[4 * 4]; px[0] = 60000;   // first data row, first column
        var plain = AutoStretch.ToBgra(FitsImage.Parse(FitsImage.Write16(4, 4, px)));
        var top = AutoStretch.ToBgra(FitsImage.Parse(FitsImage.Write16(4, 4, px, new Dictionary<string, string> { ["ROWORDER"] = "'TOP-DOWN'" })));
        Assert.True(plain[(3 * 4 + 0) * 4] > 200);   // bottom-up: the first data row is drawn last
        Assert.True(top[0] > 200);                    // top-down: first row is the top row
    }

    [Fact]
    public void MtfFixedPoints()
    {
        Assert.Equal(0, AutoStretch.Mtf(0.3, 0), 9);
        Assert.Equal(1, AutoStretch.Mtf(0.3, 1), 9);
        Assert.Equal(0.5, AutoStretch.Mtf(0.3, 0.3), 9);
    }
}

public class FitsHeaderTests
{
    [Fact]
    public void AddsAndReplacesCardsWithoutTouchingTheData()
    {
        var px = new ushort[8 * 8]; for (int i = 0; i < px.Length; i++) px[i] = (ushort)(i * 500);
        var original = FitsImage.Write16(8, 8, px, new Dictionary<string, string> { ["EXPTIME"] = "5.0" });
        var edited = FitsHeader.Set(original, new Dictionary<string, string>
        {
            ["EXPTIME"] = FitsHeader.NumberCard("EXPTIME", 30.5, "exposure"),
            ["OBJECT"] = FitsHeader.StringCard("OBJECT", "M 42"),
            ["OBSNOTE"] = FitsHeader.StringCard("OBSNOTE", "it's fine"),
        });
        Assert.Equal(0, edited.Length % 2880);
        var img = FitsImage.Parse(edited);
        Assert.Equal(30.5, img.GetDouble("EXPTIME"));
        Assert.Equal("M 42", img.Get("OBJECT"));
        Assert.Equal("it's fine", img.Get("OBSNOTE"));                                // doubled quotes round-trip
        for (int i = 0; i < px.Length; i++) Assert.Equal(px[i], img.Data[i]);       // pixels untouched
    }

    [Fact]
    public void GrowsTheHeaderByWholeBlocks()
    {
        var original = FitsImage.Write16(4, 4, new ushort[16]);
        var many = Enumerable.Range(0, 60).ToDictionary(i => $"KEY{i:000}", i => FitsHeader.NumberCard($"KEY{i:000}", i));
        var edited = FitsHeader.Set(original, many);
        Assert.Equal(original.Length + 2880, edited.Length);                      // 68 cards no longer fit one block, two do
        Assert.Equal(59, FitsImage.Parse(edited).GetDouble("KEY059"));
    }
}
