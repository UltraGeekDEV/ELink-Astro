using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ELink.UI.Controls;

/// <summary>Smooth pseudo-random noise (value noise, summed over octaves): the texture of nebulae, the mottling of the Milky Way.
/// Deterministic: the same seed and position always give the same value.</summary>
public static class Noise
{
    private static uint Hash(int x, int y, int z, uint seed)
    {
        uint h = seed ^ 0x9E3779B9u;
        h ^= (uint)x * 0x85EBCA6Bu; h = (h << 13) | (h >> 19);
        h ^= (uint)y * 0xC2B2AE35u; h = (h << 11) | (h >> 21);
        h ^= (uint)z * 0x27D4EB2Fu; h *= 0x165667B1u;
        h ^= h >> 15; h *= 0x85EBCA6Bu; h ^= h >> 13;
        return h;
    }

    private static double Lattice(int x, int y, int z, uint seed) => Hash(x, y, z, seed) / (double)uint.MaxValue;
    private static double Smooth(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>Value noise in 3D, 0..1.</summary>
    public static double Value(double x, double y, double z, uint seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        double tx = Smooth(x - x0), ty = Smooth(y - y0), tz = Smooth(z - z0);
        double L(int dx, int dy, int dz) => Lattice(x0 + dx, y0 + dy, z0 + dz, seed);
        double a = L(0, 0, 0) + (L(1, 0, 0) - L(0, 0, 0)) * tx, b = L(0, 1, 0) + (L(1, 1, 0) - L(0, 1, 0)) * tx;
        double c = L(0, 0, 1) + (L(1, 0, 1) - L(0, 0, 1)) * tx, d = L(0, 1, 1) + (L(1, 1, 1) - L(0, 1, 1)) * tx;
        double e = a + (b - a) * ty, f = c + (d - c) * ty;
        return e + (f - e) * tz;
    }

    /// <summary>Fractal noise: octaves of value noise, each twice as fine and half as strong, 0..1.</summary>
    public static double Fbm(double x, double y, double z, uint seed, int octaves = 5)
    {
        double sum = 0, amp = 0.5, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Value(x * f, y * f, z * f, seed + (uint)o * 7919); norm += amp; amp *= 0.5; f *= 2.03;
            (x, y, z) = Turn(x, y, z);
        }
        return sum / norm;
    }

    /// <summary>Ridged fractal noise: thin bright filaments where the noise crosses its middle, 0..1.</summary>
    public static double Ridged(double x, double y, double z, uint seed, int octaves = 5)
    {
        double sum = 0, amp = 0.5, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            double n = 1 - Math.Abs(2 * Value(x * f, y * f, z * f, seed + (uint)o * 104729) - 1);
            sum += amp * n * n; norm += amp; amp *= 0.5; f *= 2.07;
            (x, y, z) = Turn(x, y, z);
        }
        return sum / norm;
    }

    /// <summary>A fixed turn (about 37 degrees round a slanted axis): each octave's lattice lies at another angle, so none of them
    /// line up into streaks.</summary>
    private static (double, double, double) Turn(double x, double y, double z) =>
        (0.7986 * x - 0.4314 * y + 0.4196 * z + 3.7, 0.5638 * x + 0.7681 * y - 0.3036 * z + 1.3, -0.2122 * x + 0.4733 * y + 0.8547 * z + 5.1);

    /// <summary>A stable seed from a name.</summary>
    public static uint Seed(string s) { uint h = 2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h; }
}

/// <summary>Pictures of deep-sky objects, drawn procedurally to look like they do in a photograph rather than as outlines:
/// emission nebulae as hydrogen-red clouds with filaments, reflection nebulae as blue haze, supernova remnants as filament rings,
/// planetaries as small rings, galaxies as a bright core in a soft disc (dust lanes when seen edge-on), globulars as a dense
/// glow. Each is made once per object and size, and drawn stretched over the object's catalogue ellipse.</summary>
public static class DsoTextures
{
    public enum Look { Emission, Reflection, Remnant, Planetary, Galaxy, Globular }

    private static readonly Dictionary<(string, int, int, Look), WriteableBitmap> Cache = new();

    /// <summary>The texture for an object at about this many pixels across (rounded up to a cached size). Its square covers the
    /// catalogue ellipse stretched to a circle, plus the glow beyond it.</summary>
    public static WriteableBitmap Get(string id, Look look, double minorOverMajor, double pixelsAcross)
    {
        int size = pixelsAcross <= 24 ? 24 : pixelsAcross <= 48 ? 48 : pixelsAcross <= 96 ? 96 : pixelsAcross <= 192 ? 192 : 320;
        int q = Math.Clamp((int)Math.Round(minorOverMajor * 20), 1, 20);     // the shape, in twentieths: a thin object gets a thin texture
        lock (Cache)
        {
            if (Cache.TryGetValue((id, size, q, look), out var cached)) return cached;
            var bmp = Make(id, look, q / 20.0, size);
            if (Cache.Count > 600) Cache.Clear();          // a long session panning all over the sky: start again
            Cache[(id, size, q, look)] = bmp;
            return bmp;
        }
    }

    /// <summary>How far the texture reaches beyond the catalogue ellipse (its square is this many times the ellipse).</summary>
    public const double Reach = 1.45;

    private static WriteableBitmap Make(string id, Look look, double q, int size)
    {
        // as tall as the object is long, as wide as it is broad: the texture is drawn at its own shape, so its noise is not
        // stretched into streaks
        uint seed = Noise.Seed(id);
        int width = Math.Max(8, (int)Math.Round(size * q)), height = size;
        var px = new int[width * height];
        // features scale with the object, not the texture: a big nebula has more structure across it
        double detail = look switch { Look.Galaxy => 3.0, Look.Globular => 9.0, Look.Planetary => 3.5, _ => 4.5 };
        for (int j = 0; j < height; j++)
            for (int i = 0; i < width; i++)
            {
                // -Reach..Reach across, the catalogue ellipse is the unit circle; the noise is taken in the object's own proportions
                double x = ((i + 0.5) / width * 2 - 1) * Reach, y = ((j + 0.5) / height * 2 - 1) * Reach;
                double r = Math.Sqrt(x * x + y * y);
                double nx = x * q * detail, ny = y * detail;
                double v; (double R, double G, double B) col;
                switch (look)
                {
                    case Look.Galaxy:
                    {
                        // a bulge and an exponential disc; seen edge-on (narrow), a dark dust lane along the long axis
                        double disc = Math.Exp(-r / 0.26) * 0.75, bulge = Math.Exp(-(r * r) / (0.07 * 0.07)) * 1.6 + Math.Exp(-r / 0.09) * 0.5;
                        double arms = 0.8 + 0.4 * Noise.Fbm(nx * 1.6, ny * 1.6, 0, seed, 4);
                        // seen edge-on (narrow): a dust lane along the long axis, off to the near side of the middle and broken up
                        double lane = q < 0.45 ? 1 - 0.38 * Math.Exp(-((x - 0.16) * (x - 0.16)) / 0.012) * Math.Exp(-(y * y) / 0.45) * (0.6 + 0.8 * Noise.Fbm(nx * 3, ny * 3, 4, seed, 3)) : 1;
                        v = (disc * arms + bulge) * lane;
                        double core = Math.Clamp(bulge / (disc + bulge + 1e-9), 0, 1);
                        col = (255, 228 + 12 * (1 - core), 190 + 50 * (1 - core));
                        v = (1 - Math.Exp(-v * 1.4)) * Edge(r, 1.15);        // a photograph's soft roll-off, not a clipped plateau
                        break;
                    }
                    case Look.Globular:
                    {
                        double n = Noise.Value(nx * 4, ny * 4, 0, seed);
                        v = Math.Exp(-(r * r) / (0.28 * 0.28)) * 1.1 + Math.Exp(-r / 0.35) * 0.35;
                        v *= 0.75 + 0.5 * (n > 0.86 ? 1 : n * 0.5);       // resolved stars sparkle over the glow
                        col = (255, 236, 200);
                        v = Math.Clamp(v, 0, 1) * Edge(r, 1.1);
                        break;
                    }
                    case Look.Planetary:
                    {
                        double ring = Math.Exp(-Math.Pow((r - 0.62) / 0.22, 2)), inner = Math.Exp(-(r * r) / 0.25) * 0.45;
                        v = (ring + inner) * (0.8 + 0.4 * Noise.Fbm(nx, ny, 3, seed, 3));
                        col = r < 0.5 ? (150, 240, 230) : (110, 220, 210);
                        v = Math.Clamp(v, 0, 1) * Edge(r, 1.05);
                        break;
                    }
                    case Look.Remnant:
                    {
                        double shell = Math.Exp(-Math.Pow((r - 0.8) / 0.28, 2));
                        double fil = Noise.Ridged(nx, ny, 5, seed, 5);
                        v = shell * Math.Pow(fil, 2.2) * 1.5 + shell * 0.08;
                        col = Noise.Value(nx * 0.5, ny * 0.5, 9, seed) > 0.55 ? (110, 200, 255) : (255, 95, 90);   // oxygen blue and hydrogen red filaments
                        v = Math.Clamp(v, 0, 1) * Edge(r, 1.2);
                        break;
                    }
                    default:
                    {
                        // clouds: a soft falloff from the middle, broken up by fractal noise, with a few brighter filaments
                        double body = Math.Exp(-Math.Pow(r / 0.72, 2) * 1.6);
                        double cloud = Noise.Fbm(nx, ny, 0, seed, 6);
                        double fil = Noise.Ridged(nx * 1.3, ny * 1.3, 2, seed, 4);
                        double s = Math.Clamp((cloud - 0.32) * 1.9, 0, 1);
                        v = body * (Math.Pow(s, 1.6) * 0.9 + Math.Pow(fil, 4) * 0.45) + body * body * 0.12;
                        if (look == Look.Reflection) col = (125, 160, 255);
                        else
                        {
                            // hydrogen red, going pink-white where it is brightest (a photograph's saturated core)
                            double hot = Math.Clamp((v - 0.45) * 1.8, 0, 1);
                            col = (255, 70 + 140 * hot, 100 + 120 * hot);
                        }
                        v = Math.Clamp(v * 1.15, 0, 1) * Edge(r, Reach * 0.95);
                        break;
                    }
                }
                double a = Math.Clamp(v, 0, 1);
                int A = (int)(255 * a), R = (int)(col.R * a), G = (int)(col.G * a), B = (int)(col.B * a);
                px[j * width + i] = (A << 24) | (R << 16) | (G << 8) | B;
            }
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
            for (int j = 0; j < height; j++) System.Runtime.InteropServices.Marshal.Copy(px, j * width, fb.Address + j * fb.RowBytes, width);
        return bmp;
    }

    /// <summary>A smooth fade to nothing at <paramref name="end"/> (no hard edge where the texture's square ends).</summary>
    private static double Edge(double r, double end) => r >= end ? 0 : 1 - Math.Pow(r / end, 6);
}
