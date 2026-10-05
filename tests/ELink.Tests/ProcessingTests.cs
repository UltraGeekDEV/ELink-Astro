using System.IO.Compression;
using ELink.Imaging.Processing;
using Xunit;

namespace ELink.Tests;

/// <summary>Turning a linear stack into a picture: gradient removal, neutral sky, the automatic stretch, PNG output.</summary>
public class ProcessingTests
{
    private const int W = 400, H = 300;

    /// <summary>A sky with a tilt and a glow, noise, a faint nebula and a few stars. <paramref name="skies"/> are the channels' sky levels.</summary>
    private static float[] Sky(int channels, double[] skies, double tilt = 300, double glow = 200, double seed = 1)
    {
        var rnd = new Random((int)seed);
        var d = new float[W * H * channels];
        var stars = new (double X, double Y)[] { (60, 50), (320, 80), (200, 150), (90, 240), (350, 250), (240, 40) };
        for (int c = 0; c < channels; c++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double u = x / (double)W - 0.5, v = y / (double)H - 0.5;
                    double g = tilt * (u + 0.5) * (c + 1) / channels + glow * Math.Exp(-((u - 0.3) * (u - 0.3) + (v + 0.3) * (v + 0.3)) / 0.05);
                    double val = skies[c] + g + (rnd.NextDouble() - 0.5) * 20;
                    val += 120 * Math.Exp(-(((x - 200) * (x - 200)) + ((y - 150) * (y - 150))) / (2 * 40 * 40));   // faint nebula
                    foreach (var (sx, sy) in stars) val += 20000 * Math.Exp(-((x - sx) * (x - sx) + (y - sy) * (y - sy)) / (2 * 2.0 * 2.0));
                    d[c * W * H + y * W + x] = (float)val;
                }
        return d;
    }

    private static double Mean(float[] d, int c, int x0, int y0, int size)
    {
        double s = 0; int n = 0;
        for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) { s += d[c * W * H + y * W + x]; n++; }
        return s / n;
    }

    [Fact]
    public void GradientRemovalLevelsTheSkyAndKeepsStarsAndNebula()
    {
        var sky = Sky(1, [1000]);
        var linear = new ProcessingParams { Stretch = false, RemoveGradient = false, NeutralizeBackground = false, BlackClipSigmas = 0 };
        var without = PictureProcessor.Process(sky, W, H, 1, linear);
        var with = PictureProcessor.Process(sky, W, H, 1, linear with { RemoveGradient = true, GradientDegree = 5 });
        double Spread(ProcessedPicture p) { var corners = new[] { Mean(p.Display, 0, 5, 5, 20), Mean(p.Display, 0, W - 25, 5, 20), Mean(p.Display, 0, 5, H - 25, 20), Mean(p.Display, 0, W - 25, H - 25, 20) }; return corners.Max() - corners.Min(); }
        double before = Spread(without), after = Spread(with);
        Assert.True(before > 0.002, $"the gradient is there to begin with: {before}");
        Assert.True(after < before / 4, $"the corners should agree after: {before} -> {after}");
        Assert.True(with.GradientPercent > 10, $"it says how much it took out: {with.GradientPercent}");
        // a star and the nebula are still there: they were not taken for sky
        float star = with.Display[50 * W + 60], skyPx = with.Display[10 * W + 200];
        Assert.True(star > skyPx + 0.2f, $"star {star} sky {skyPx}");
        double nebula = Mean(with.Display, 0, 190, 140, 20), around = Mean(with.Display, 0, 20, 140, 20);
        Assert.True(nebula > around, $"nebula {nebula} vs {around}");
    }

    [Fact]
    public void TheStretchPutsTheSkyWhereAsked()
    {
        var sky = Sky(1, [800], tilt: 0, glow: 0);
        foreach (double level in new[] { 0.15, 0.25, 0.4 })
        {
            var p = PictureProcessor.Process(sky, W, H, 1, new ProcessingParams { BackgroundLevel = level });
            double med = p.Display.OrderBy(x => x).ElementAt(p.Display.Length / 2);
            Assert.InRange(med, level - 0.04, level + 0.04);
            Assert.All(p.Display, v => Assert.InRange(v, 0f, 1f));
        }
        // a linear "stretch" leaves the sky dark
        var flat = PictureProcessor.Process(sky, W, H, 1, new ProcessingParams { Stretch = false });
        Assert.True(flat.Display.OrderBy(x => x).ElementAt(flat.Display.Length / 2) < 0.1);
    }

    [Fact]
    public void ColourSkiesAreMadeNeutralAndLinkedStretchKeepsColour()
    {
        var sky = Sky(3, [1000, 1400, 800], tilt: 0, glow: 0);
        var picture = PictureProcessor.Process(sky, W, H, 3, new ProcessingParams { RemoveGradient = false });
        int plane = W * H;
        double Med(int c) => picture.Display.Skip(c * plane).Take(plane).OrderBy(x => x).ElementAt(plane / 2);
        Assert.InRange(Math.Abs(Med(0) - Med(1)), 0, 0.03); Assert.InRange(Math.Abs(Med(1) - Med(2)), 0, 0.03);
        var tinted = PictureProcessor.Process(sky, W, H, 3, new ProcessingParams { RemoveGradient = false, NeutralizeBackground = false });
        double TMed(int c) => tinted.Display.Skip(c * plane).Take(plane).OrderBy(x => x).ElementAt(plane / 2);
        Assert.True(Math.Abs(TMed(0) - TMed(1)) > 0.05, "without it the sky keeps its cast");
        // saturation 0 is grey
        var grey = PictureProcessor.Process(sky, W, H, 3, new ProcessingParams { RemoveGradient = false, Saturation = 0 });
        for (int i = 0; i < plane; i += 997) Assert.Equal((double)grey.Display[i], grey.Display[plane + i], 3);
        // green reduction never raises green
        var base3 = PictureProcessor.Process(Sky(3, [1000, 1100, 1000], 0, 0), W, H, 3, new ProcessingParams { RemoveGradient = false, NeutralizeBackground = false });
        var less = PictureProcessor.Process(Sky(3, [1000, 1100, 1000], 0, 0), W, H, 3, new ProcessingParams { RemoveGradient = false, NeutralizeBackground = false, GreenReduction = 1 });
        for (int i = 0; i < plane; i += 997) Assert.True(less.Display[plane + i] <= base3.Display[plane + i] + 1e-6);
    }

    [Fact]
    public void NaNPixelsAreSkyAndTheHistogramIsFilled()
    {
        var sky = Sky(1, [1000], 0, 0);
        for (int i = 0; i < 5000; i++) sky[i] = float.NaN;
        var p = PictureProcessor.Process(sky, W, H, 1, new ProcessingParams());
        Assert.All(p.Display, v => Assert.False(float.IsNaN(v)));
        Assert.All(p.Display.Take(5000), v => Assert.Equal(0f, v));                  // nothing there: black
        double med = p.Display.Skip(6000).OrderBy(x => x).ElementAt((p.Display.Length - 6000) / 2);
        Assert.InRange(med, 0.2, 0.3);                                               // and it does not disturb the sky
        Assert.Equal(PictureProcessor.HistogramBins, p.Histogram.Length);
        Assert.Contains(p.Histogram, v => v > 0.9);
        Assert.Contains("stretched", p.Note);
    }

    [Fact]
    public void PngsAreWellFormedInEightAndSixteenBits()
    {
        var data = new float[W * H * 3];
        for (int i = 0; i < data.Length; i++) data[i] = i % 256 / 255f;
        foreach (var sixteen in new[] { false, true })
        {
            var png = PngWriter.Encode(data, W, H, 3, sixteen);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
            int pos = 8; var chunks = new List<string>(); using var idat = new MemoryStream(); int width = 0, depth = 0, colour = -1;
            while (pos < png.Length)
            {
                int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                var body = png.AsSpan(pos + 8, len);
                uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
                // CRC-32 of type + data
                uint c = 0xFFFFFFFFu; foreach (var b in png.AsSpan(pos + 4, 4 + len)) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
                Assert.Equal(c ^ 0xFFFFFFFFu, crc);
                chunks.Add(type);
                if (type == "IHDR") { width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body); depth = body[8]; colour = body[9]; }
                if (type == "IDAT") idat.Write(body);
                pos += 12 + len;
            }
            Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);
            Assert.Equal((W, sixteen ? 16 : 8, 2), (width, depth, colour));
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream(); z.CopyTo(raw);
            Assert.Equal((W * 3 * (sixteen ? 2 : 1) + 1) * H, raw.Length);
        }
        // the first row of a flipped picture is the last row of the data
        var g = new float[4 * 2]; for (int i = 0; i < 4; i++) { g[i] = 0f; g[4 + i] = 1f; }
        byte First(byte[] png)
        {
            using var idat = new MemoryStream(); int pos = 8;
            while (pos < png.Length) { int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos)); if (System.Text.Encoding.ASCII.GetString(png, pos + 4, 4) == "IDAT") idat.Write(png.AsSpan(pos + 8, len)); pos += 12 + len; }
            idat.Position = 0; using var z = new ZLibStream(idat, CompressionMode.Decompress); using var raw = new MemoryStream(); z.CopyTo(raw);
            return raw.ToArray()[1];
        }
        Assert.Equal(0, First(PngWriter.Encode(g, 4, 2, 1)));
        Assert.Equal(255, First(PngWriter.Encode(g, 4, 2, 1, flipY: true)));
    }
}
