namespace ELink.Imaging;

public enum Interpolation { Nearest, Bilinear, Bicubic }

public readonly record struct StackAddResult(bool Added, int PixelsTouched, int BinFactor, int Subsamples, string Message);

/// <summary>Accumulates frames into a fixed sky grid (the stack's own TAN WCS and pixel scale). Every frame is resampled
/// through the exact homography between its WCS and the grid. Finer output than input: the frame is interpolated
/// (bicubic by default). Coarser output: the frame is first box-binned by the integer part of the scale ratio, then each
/// output pixel averages a grid of samples over its own area, so downscaling is an area average, not a decimation.</summary>
public sealed class LiveStacker
{
    public TanWcs Wcs { get; }
    public int Width { get; }
    public int Height { get; }
    public Interpolation Interpolation { get; set; } = Interpolation.Bicubic;
    public int Frames { get; private set; }
    /// <summary>1 (mono) or 3 (RGB). Set by the first frame: a colour first frame makes a colour stack. Later mono frames
    /// go into all three channels; later colour frames into a mono stack go in as their luminance.</summary>
    public int Channels { get; private set; } = 1;

    private float[][] _sum;
    private readonly float[] _weight;
    private readonly object _gate = new();

    public LiveStacker(TanWcs wcs, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Wcs = wcs; Width = width; Height = height;
        _sum = [new float[(long)width * height]];
        _weight = new float[(long)width * height];
    }

    public void Reset()
    {
        lock (_gate) { foreach (var s in _sum) Array.Clear(s); Array.Clear(_weight); Frames = 0; }
    }

    /// <summary>Adds one mono frame (row-major in file order, as <see cref="FitsImage.Data"/>) whose sky position is
    /// <paramref name="frameWcs"/>. <paramref name="background"/> is subtracted first.</summary>
    public StackAddResult Add(float[] data, int width, int height, TanWcs frameWcs, float background = 0, float weight = 1) =>
        Add(data, width, height, 1, frameWcs, [background], weight);

    /// <summary>Adds a frame of 1 or 3 planar channels; <paramref name="backgrounds"/> has one level per channel.</summary>
    public StackAddResult Add(float[] data, int width, int height, int channels, TanWcs frameWcs, float[] backgrounds, float weight = 1)
    {
        if (channels is not (1 or 3)) throw new ArgumentException("1 or 3 channels", nameof(channels));
        if (data.Length < (long)width * height * channels) throw new ArgumentException("data is smaller than width x height x channels");
        if (backgrounds.Length < channels) throw new ArgumentException("one background per channel", nameof(backgrounds));
        double ratio = Wcs.PixelScaleArcsec / frameWcs.PixelScaleArcsec;
        int bin = ratio >= 2 ? (int)Math.Floor(ratio) : 1;
        if (bin > 1) (data, width, height, frameWcs) = Bin(data, width, height, frameWcs, bin, channels);
        double rest = ratio / bin;
        int k = rest > 1.0001 ? (int)Math.Ceiling(rest) : 1;    // samples per axis inside one output pixel

        // where the frame lands on the grid: its corners, through the reverse homography
        var toGrid = frameWcs.HomographyTo(Wcs);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in new[] { (-0.5, -0.5), (width - 0.5, -0.5), (-0.5, height - 0.5), (width - 0.5, height - 0.5) })
        {
            var (gx, gy) = Apply(toGrid, cx, cy);
            if (double.IsNaN(gx)) return new(false, 0, bin, k, "the frame is not on the same side of the sky as the stack");
            minX = Math.Min(minX, gx); maxX = Math.Max(maxX, gx); minY = Math.Min(minY, gy); maxY = Math.Max(maxY, gy);
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(maxX));
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
        if (x0 > x1 || y0 > y1) return new(false, 0, bin, k, "the frame does not overlap the stack's field");

        var h = Wcs.HomographyTo(frameWcs);
        var interp = k > 1 ? Interpolation.Bilinear : Interpolation;
        float invK2 = 1f / (k * k);
        int touched = 0;
        var src = data; int sw = width, sh = height, fc = channels;
        long plane = (long)sw * sh;
        var bg = backgrounds;
        lock (_gate)
        {
            if (Frames == 0 && Channels != channels)
            {
                Channels = channels;
                _sum = Enumerable.Range(0, channels).Select(_ => new float[_weight.Length]).ToArray();
            }
            var sums = _sum; int sc = Channels;
            Parallel.For(y0, y1 + 1, () => 0, (y, _, count) =>
            {
                Span<float> acc = stackalloc float[3];
                long row = (long)y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    acc.Clear(); int n = 0;
                    for (int v = 0; v < k; v++)
                    {
                        double py = y + (v + 0.5) / k - 0.5;
                        for (int u = 0; u < k; u++)
                        {
                            double px = x + (u + 0.5) / k - 0.5;
                            double w = h[6] * px + h[7] * py + h[8];
                            if (w <= 0) continue;
                            double sx = (h[0] * px + h[1] * py + h[2]) / w, sy = (h[3] * px + h[4] * py + h[5]) / w;
                            if (sx < -0.5 || sy < -0.5 || sx > sw - 0.5 || sy > sh - 0.5) continue;
                            for (int c = 0; c < fc; c++) acc[c] += Sample(src, c * plane, sw, sh, sx, sy, interp) - bg[c];
                            n++;
                        }
                    }
                    if (n == 0) continue;
                    float f = weight * invK2;
                    if (sc == fc) for (int c = 0; c < sc; c++) sums[c][row + x] += f * acc[c];
                    else if (sc == 3) for (int c = 0; c < 3; c++) sums[c][row + x] += f * acc[0];      // mono into colour: grey
                    else sums[0][row + x] += f * (acc[0] + acc[1] + acc[2]) / 3;                       // colour into mono: luminance
                    _weight[row + x] += weight * n * invK2;
                    count++;
                }
                return count;
            }, c => Interlocked.Add(ref touched, c));
            if (touched > 0) Frames++;
        }
        return touched > 0 ? new(true, touched, bin, k, "") : new(false, 0, bin, k, "the frame does not overlap the stack's field");
    }

    /// <summary>The stack: weighted mean per pixel, planar (<see cref="Channels"/> planes), NaN where nothing has landed.</summary>
    public float[] Mean()
    {
        lock (_gate)
        {
            long plane = _weight.Length;
            var r = new float[plane * Channels];
            for (int c = 0; c < Channels; c++)
            {
                var s = _sum[c];
                for (long i = 0; i < plane; i++) r[c * plane + i] = _weight[i] > 1e-6f ? s[i] / _weight[i] : float.NaN;
            }
            return r;
        }
    }

    /// <summary>Share of the grid that has received any data.</summary>
    public double Coverage()
    {
        long n = 0;
        lock (_gate) foreach (var w in _weight) if (w > 1e-6f) n++;
        return (double)n / _weight.Length;
    }

    /// <summary>The stack area-averaged down to fit in maxWidth x maxHeight (never up), planar, with its WCS.</summary>
    public (float[] Data, int Width, int Height, TanWcs Wcs) Reduced(int maxWidth, int maxHeight)
    {
        int f = Math.Max(1, Math.Max((int)Math.Ceiling(Width / (double)Math.Max(1, maxWidth)), (int)Math.Ceiling(Height / (double)Math.Max(1, maxHeight))));
        if (f == 1) return (Mean(), Width, Height, Wcs);
        int w = Width / f, hgt = Height / f;
        lock (_gate)
        {
            int channels = Channels;
            long plane = (long)w * hgt;
            var r = new float[plane * channels];
            Parallel.For(0, hgt, y =>
            {
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < channels; c++)
                    {
                        double s = 0, wt = 0;
                        for (int dy = 0; dy < f; dy++)
                        {
                            long row = (long)(y * f + dy) * Width + x * f;
                            for (int dx = 0; dx < f; dx++) { s += _sum[c][row + dx]; wt += _weight[row + dx]; }
                        }
                        r[c * plane + (long)y * w + x] = wt > 1e-6 ? (float)(s / wt) : float.NaN;
                    }
            });
            return (r, w, hgt, Binned(Wcs, f));
        }
    }

    /// <summary>Median of a sample of the pixels: the sky background level.</summary>
    public static float Background(float[] data)
    {
        int step = Math.Max(1, data.Length / 60000);
        var sample = new List<float>(data.Length / step + 1);
        for (int i = 0; i < data.Length; i += step) if (!float.IsNaN(data[i])) sample.Add(data[i]);
        if (sample.Count == 0) return 0;
        sample.Sort();
        return sample[sample.Count / 2];
    }

    /// <summary>Box-bins every channel by an integer factor (area average); the WCS follows.</summary>
    public static (float[] Data, int Width, int Height, TanWcs Wcs) Bin(float[] data, int width, int height, TanWcs wcs, int f, int channels = 1)
    {
        int w = width / f, h = height / f;
        long srcPlane = (long)width * height, plane = (long)w * h;
        var r = new float[plane * channels];
        float inv = 1f / (f * f);
        Parallel.For(0, h * channels, yc =>
        {
            int y = yc % h, c = yc / h;
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    long row = c * srcPlane + (long)(y * f + dy) * width + x * f;
                    for (int dx = 0; dx < f; dx++) s += data[row + dx];
                }
                r[c * plane + (long)y * w + x] = s * inv;
            }
        });
        return (r, w, h, Binned(wcs, f));
    }

    /// <summary>The WCS of an image binned f x f: binned pixel X covers source pixels f·X .. f·X + f - 1.</summary>
    public static TanWcs Binned(TanWcs wcs, int f) => wcs with
    {
        CrPix1 = (wcs.CrPix1 - (f - 1) / 2.0) / f, CrPix2 = (wcs.CrPix2 - (f - 1) / 2.0) / f,
        Cd11 = wcs.Cd11 * f, Cd12 = wcs.Cd12 * f, Cd21 = wcs.Cd21 * f, Cd22 = wcs.Cd22 * f,
    };

    private static (double X, double Y) Apply(double[] h, double x, double y)
    {
        double w = h[6] * x + h[7] * y + h[8];
        if (w <= 0) return (double.NaN, double.NaN);
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    private static float Sample(float[] all, long offset, int w, int h, double x, double y, Interpolation mode)
    {
        var d = all.AsSpan((int)offset, w * h);
        switch (mode)
        {
            case Interpolation.Nearest:
                return d[Math.Clamp((int)Math.Round(y), 0, h - 1) * w + Math.Clamp((int)Math.Round(x), 0, w - 1)];
            case Interpolation.Bilinear:
            {
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                float fx = (float)(x - ix), fy = (float)(y - iy);
                int xa = Math.Clamp(ix, 0, w - 1), xb = Math.Clamp(ix + 1, 0, w - 1), ya = Math.Clamp(iy, 0, h - 1), yb = Math.Clamp(iy + 1, 0, h - 1);
                float top = d[ya * w + xa] * (1 - fx) + d[ya * w + xb] * fx;
                float bot = d[yb * w + xa] * (1 - fx) + d[yb * w + xb] * fx;
                return top * (1 - fy) + bot * fy;
            }
            default:
            {
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                float fx = (float)(x - ix), fy = (float)(y - iy);
                Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
                CatmullRom(fx, wx); CatmullRom(fy, wy);
                float s = 0;
                for (int j = 0; j < 4; j++)
                {
                    int row = Math.Clamp(iy - 1 + j, 0, h - 1) * w;
                    float r = 0;
                    for (int i = 0; i < 4; i++) r += wx[i] * d[row + Math.Clamp(ix - 1 + i, 0, w - 1)];
                    s += wy[j] * r;
                }
                return s;
            }
        }
    }

    private static void CatmullRom(float t, Span<float> w)
    {
        float t2 = t * t, t3 = t2 * t;
        w[0] = 0.5f * (-t3 + 2 * t2 - t);
        w[1] = 0.5f * (3 * t3 - 5 * t2 + 2);
        w[2] = 0.5f * (-3 * t3 + 4 * t2 + t);
        w[3] = 0.5f * (t3 - t2);
    }
}
