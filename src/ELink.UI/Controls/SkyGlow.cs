namespace ELink.UI.Controls;

/// <summary>An all-sky brightness map (equirectangular: x along RA from 0h to 24h, y from Dec +90 down to -90), values 0..1. The
/// Milky Way's glow, drawn under the stars.</summary>
public sealed class SkyGlowMap
{
    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }

    public SkyGlowMap(int width, int height, float[] values) { Width = width; Height = height; Values = values; }

    /// <summary>Brightness at a sky position (bilinear, RA wraps).</summary>
    public float Sample(double raHours, double decDegrees)
    {
        double fx = ((raHours % 24 + 24) % 24) / 24 * Width - 0.5, fy = (90 - decDegrees) / 180 * Height - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy); double tx = fx - x0, ty = fy - y0;
        float V(int x, int y) { x = ((x % Width) + Width) % Width; y = Math.Clamp(y, 0, Height - 1); return Values[y * Width + x]; }
        return (float)((V(x0, y0) * (1 - tx) + V(x0 + 1, y0) * tx) * (1 - ty) + (V(x0, y0 + 1) * (1 - tx) + V(x0 + 1, y0 + 1) * tx) * ty);
    }

    /// <summary>From an atlas's map (a byte a cell, 0..255).</summary>
    public static SkyGlowMap FromBytes(int width, int height, byte[] data)
    {
        var v = new float[width * height];
        for (int i = 0; i < v.Length; i++) v[i] = data[i] / 255f;
        return new SkyGlowMap(width, height, v);
    }

    /// <summary>The glow from brightness contours (closed outlines, nested where brighter): each contour adds one level where it
    /// covers the sky, then the steps are blurred into a smooth band and scaled to 0..1.</summary>
    public static SkyGlowMap FromContours(IEnumerable<IReadOnlyList<(double RaHours, double DecDegrees)>> contours, int width = 1440, int height = 720)
    {
        var acc = new float[width * height];
        var crossings = new List<double>();
        foreach (var shape in contours)
        {
            if (shape.Count < 3) continue;
            // unwrap RA so consecutive points never jump by more than 12h (an outline crossing 0h stays one piece)
            var pts = new List<(double X, double Y)>(shape.Count);
            double prev = shape[0].RaHours * 15;
            foreach (var (ra, dec) in shape)
            {
                double x = ra * 15;
                while (x - prev > 180) x -= 360;
                while (x - prev < -180) x += 360;
                pts.Add((x, dec)); prev = x;
            }
            // a contour round the pole does not close in RA: close it over the pole
            double drift = pts[^1].X - pts[0].X;
            if (Math.Abs(drift) > 180)
            {
                double pole = pts.Average(p => p.Y) >= 0 ? 90 : -90;
                pts.Add((pts[^1].X, pole)); pts.Add((pts[0].X, pole));
            }
            for (int shift = -1; shift <= 1; shift++)
                for (int row = 0; row < height; row++)
                {
                    double y = 90 - (row + 0.5) * 180 / height;
                    crossings.Clear();
                    for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                    {
                        var (xi, yi) = pts[i]; var (xj, yj) = pts[j];
                        if ((yi > y) == (yj > y)) continue;
                        crossings.Add(xi + (y - yi) / (yj - yi) * (xj - xi) + shift * 360);
                    }
                    crossings.Sort();
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                    {
                        int c0 = (int)Math.Ceiling(crossings[k] / 360 * width - 0.5), c1 = (int)Math.Floor(crossings[k + 1] / 360 * width - 0.5);
                        for (int c = Math.Max(0, c0); c <= Math.Min(width - 1, c1); c++) acc[row * width + c] += 1;
                    }
                }
        }
        // blur: three box passes (about a gaussian), wrapping in RA
        var tmp = new float[acc.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(acc, tmp, width, height, 6);
            BoxV(tmp, acc, width, height, 6);
        }
        float max = acc.Length > 0 ? acc.Max() : 0;
        if (max > 0) for (int i = 0; i < acc.Length; i++) acc[i] /= max;
        // star clouds and dust: the smooth contours broken up by fractal noise taken on the sphere (no seam at 0h, none at the poles)
        for (int row = 0; row < height; row++)
        {
            double dec = (90 - (row + 0.5) * 180 / height) * Math.PI / 180;
            for (int c = 0; c < width; c++)
            {
                int k = row * width + c;
                if (acc[k] <= 0.001f) continue;
                double ra = (c + 0.5) / width * 2 * Math.PI;
                double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), z = Math.Sin(dec);
                double clouds = Noise.Fbm(x * 6 + 11, y * 6 + 7, z * 6 + 3, 1234567u, 5);
                double lanes = Noise.Ridged(x * 9, y * 9, z * 9, 7654321u, 4);
                double m = Math.Clamp(0.35 + 1.25 * clouds - 0.35 * lanes * acc[k], 0.1, 1.6);
                acc[k] = (float)Math.Clamp(acc[k] * m, 0, 1);
            }
        }
        return new SkyGlowMap(width, height, acc);
    }

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            double sum = 0; int row = y * w;
            for (int k = -r; k <= r; k++) sum += src[row + ((k % w) + w) % w];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (float)(sum / (2 * r + 1));
                sum += src[row + (x + r + 1) % w] - src[row + ((x - r) % w + w) % w];
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { sum += src[k * w + x]; n++; }
                dst[y * w + x] = (float)(sum / n);
            }
    }
}
