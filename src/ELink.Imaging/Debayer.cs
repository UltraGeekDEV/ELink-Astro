namespace ELink.Imaging;

public enum DebayerMode
{
    /// <summary>Leave the colour filter mosaic as it is (mono data).</summary>
    None,
    /// <summary>Every 2x2 Bayer cell becomes one RGB pixel (R, mean of the two G, B): half the width and height, no
    /// interpolation, so no colour artefacts; the pixel scale doubles.</summary>
    SuperPixel,
    /// <summary>Full resolution: each missing colour is the mean of the nearest pixels of that colour (bilinear demosaic).</summary>
    Interpolated,
}

/// <summary>A debayered image: planar R, G, B planes (each Width x Height, row 0 first as in the source), and how much
/// the pixel grid was binned (2 for super pixel), so a WCS of the raw frame can follow with <see cref="LiveStacker.Binned"/>.</summary>
public sealed record ColorImage(float[] Data, int Width, int Height, int Bin);

/// <summary>Demosaicing of one-shot-colour (Bayer) frames.</summary>
public static class Debayer
{
    public static readonly string[] Patterns = ["RGGB", "BGGR", "GRBG", "GBRG"];

    /// <summary>The frame's Bayer pattern from BAYERPAT, moved by XBAYROFF/YBAYROFF, as it applies to <see cref="FitsImage.Data"/>
    /// row 0, column 0; null for a mono frame (or a colour frame that is already debayered).</summary>
    public static string? PatternOf(FitsImage img)
    {
        if (img.Channels != 1) return null;
        var p = Normalize(img.Get("BAYERPAT"));
        if (p is null) return null;
        int xo = (int)img.GetDouble("XBAYROFF", 0), yo = (int)img.GetDouble("YBAYROFF", 0);
        return Shift(p, xo, yo);
    }

    /// <summary>"rggb", " RGGB " ... to RGGB; null if it is not one of the four Bayer patterns.</summary>
    public static string? Normalize(string? pattern)
    {
        var p = pattern?.Trim().Trim('\'').Trim().ToUpperInvariant();
        return p is not null && Patterns.Contains(p) ? p : null;
    }

    /// <summary>The pattern seen from pixel (dx, dy) instead of (0, 0): an odd offset swaps the columns or rows of the cell.</summary>
    public static string Shift(string pattern, int dx, int dy)
    {
        char At(int x, int y) => pattern[(((y + dy) % 2 + 2) % 2) * 2 + (((x + dx) % 2 + 2) % 2)];
        return new string([At(0, 0), At(1, 0), At(0, 1), At(1, 1)]);
    }

    /// <summary>0 = R, 1 = G, 2 = B for every position of the 2x2 cell, indexed [y % 2 * 2 + x % 2].</summary>
    private static int[] Cell(string pattern) => pattern.Select(c => c switch { 'R' => 0, 'G' => 1, _ => 2 }).ToArray();

    public static ColorImage Apply(float[] raw, int width, int height, string pattern, DebayerMode mode) => mode switch
    {
        DebayerMode.SuperPixel => SuperPixel(raw, width, height, pattern),
        DebayerMode.Interpolated => Bilinear(raw, width, height, pattern),
        _ => throw new ArgumentException("nothing to debayer with mode None", nameof(mode)),
    };

    public static ColorImage SuperPixel(float[] raw, int width, int height, string pattern)
    {
        var cell = Cell(pattern);
        int w = width / 2, h = height / 2, plane = w * h;
        var rgb = new float[plane * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = 0; k < 4; k++)
                {
                    float v = raw[(2 * y + k / 2) * width + 2 * x + k % 2];
                    switch (cell[k]) { case 0: r += v; break; case 1: g += v; break; default: b += v; break; }
                }
                int i = y * w + x;
                rgb[i] = r; rgb[plane + i] = g * 0.5f; rgb[2 * plane + i] = b;
            }
        });
        return new ColorImage(rgb, w, h, 2);
    }

    /// <summary>Bilinear demosaic: a missing colour is the mean of the same-colour pixels in the 3x3 neighbourhood
    /// (the 2 or 4 nearest), which is exactly bilinear interpolation on a Bayer grid. Edges mirror inwards.</summary>
    public static ColorImage Bilinear(float[] raw, int width, int height, string pattern)
    {
        var cell = Cell(pattern);
        int plane = width * height;
        var rgb = new float[plane * 3];
        Parallel.For(0, height, y =>
        {
            Span<float> sum = stackalloc float[3];
            Span<int> n = stackalloc int[3];
            for (int x = 0; x < width; x++)
            {
                int own = cell[(y & 1) * 2 + (x & 1)];
                int i = y * width + x;
                sum.Clear(); n.Clear();
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0) yy = 1; else if (yy >= height) yy = height - 2;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx; if (xx < 0) xx = 1; else if (xx >= width) xx = width - 2;
                        int c = cell[(yy & 1) * 2 + (xx & 1)];
                        if (c == own) continue;
                        sum[c] += raw[yy * width + xx]; n[c]++;
                    }
                }
                for (int c = 0; c < 3; c++) rgb[c * plane + i] = c == own ? raw[i] : n[c] > 0 ? sum[c] / n[c] : 0;
            }
        });
        return new ColorImage(rgb, width, height, 1);
    }

    /// <summary>For display: a raw Bayer frame as a colour <see cref="FitsImage"/> (super pixel: fast and artefact free);
    /// anything else unchanged.</summary>
    public static FitsImage ForDisplay(FitsImage img)
    {
        if (PatternOf(img) is not { } p || img.Width < 2 || img.Height < 2) return img;
        var c = SuperPixel(img.Data, img.Width, img.Height, p);
        var header = img.Header.Where(kv => kv.Key != "BAYERPAT").ToDictionary(kv => kv.Key, kv => kv.Value);
        return FitsImage.FromPlanar(c.Width, c.Height, 3, c.Data, header, img.Range);
    }
}
