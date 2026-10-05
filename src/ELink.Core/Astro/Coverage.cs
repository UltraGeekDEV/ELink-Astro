namespace ELink.Core.Astro;

/// <summary>Where the scope is, in the painted area's own frame: degrees from its centre (x along the area's width,
/// y along its height), plus the sky position angle the scope's field is turned to.</summary>
public readonly record struct Pose(double X, double Y, double FieldAngle);

/// <summary>One frame a visit takes: half sizes, how it is turned on the scope, and where it looks (scope-axis frame).
/// <paramref name="Mask"/> says which layers of the coverage map it adds exposure to (bit n = layer n); <paramref name="Scale"/> is its
/// pixel scale in arcseconds (0 = not known), what the layers' resolution ranges are matched against.</summary>
public readonly record struct FrameSpec(double HalfWidth, double HalfHeight, double Rotation, double OffsetEast, double OffsetNorth, int Mask = 1, double Scale = 0);

/// <summary>A rotated rectangle in the area's frame, with the layers it feeds.</summary>
public readonly record struct Footprint(double Cx, double Cy, double HalfWidth, double HalfHeight, double Theta, int Mask = 1);

/// <summary>Exposure time received by every spot of the area, summed over all frames, in any number of layers (channels): a
/// layer is one kind of data (a filter, a range of pixel scales) with its own depth. The area is a grid of small cells; a shot
/// adds its exposure, in each layer its frames feed, to each cell its frames' footprints cover.</summary>
public sealed class CoverageMap
{
    public const double Deg2Rad = Math.PI / 180.0;
    public int Cols { get; }
    public int Rows { get; }
    public double FovWidth { get; }
    public double FovHeight { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    /// <summary>How many layers (channels) the map has.</summary>
    public int Layers { get; }
    /// <summary>The seconds of every layer, one grid each (row-major from the north-west).</summary>
    public float[][] Planes { get; }
    /// <summary>The first layer's grid (the only one of a map with one layer).</summary>
    public float[] Seconds => Planes[0];

    public CoverageMap(double fovWidth, double fovHeight, double cell, int layers = 1)
    {
        FovWidth = fovWidth; FovHeight = fovHeight;
        Cols = Math.Max(1, (int)Math.Round(fovWidth / cell));
        Rows = Math.Max(1, (int)Math.Round(fovHeight / cell));
        CellWidth = fovWidth / Cols; CellHeight = fovHeight / Rows;
        Layers = Math.Max(1, layers);
        Planes = Enumerable.Range(0, Layers).Select(_ => new float[Cols * Rows]).ToArray();
    }

    private CoverageMap(CoverageMap other)
    {
        FovWidth = other.FovWidth; FovHeight = other.FovHeight; Cols = other.Cols; Rows = other.Rows;
        CellWidth = other.CellWidth; CellHeight = other.CellHeight; Layers = other.Layers;
        Planes = other.Planes.Select(p => (float[])p.Clone()).ToArray();
    }

    /// <summary>A map restored from saved numbers (the grid exactly as it was; the layers' grids one after another).</summary>
    public static CoverageMap Restore(double fovWidth, double fovHeight, int cols, int rows, float[] seconds, int layers = 1)
    {
        if (seconds.Length != cols * rows * layers) throw new ArgumentException("seconds do not fit the grid");
        var m = new CoverageMap(new CoverageMap(fovWidth, fovHeight, fovWidth / cols), cols, rows, layers);
        for (int l = 0; l < layers; l++) Array.Copy(seconds, l * cols * rows, m.Planes[l], 0, cols * rows);
        return m;
    }

    private CoverageMap(CoverageMap shape, int cols, int rows, int layers = 1)
    {
        FovWidth = shape.FovWidth; FovHeight = shape.FovHeight; Cols = cols; Rows = rows;
        CellWidth = FovWidth / cols; CellHeight = FovHeight / rows; Layers = layers;
        Planes = Enumerable.Range(0, layers).Select(_ => new float[cols * rows]).ToArray();
    }

    /// <summary>All the layers' seconds in one array, one grid after the other (what <see cref="Restore"/> takes).</summary>
    public float[] Flatten() => Planes.SelectMany(p => p).ToArray();

    /// <summary>An exact copy: same grid (cells need not be square), same seconds.</summary>
    public CoverageMap Clone() => new(this);

    /// <summary>Footprints of a visit's frames. Frame offsets turn with the scope's field; each frame's own rotation adds to it.</summary>
    public static Footprint[] Footprints(Pose pose, IReadOnlyList<FrameSpec> frames, double areaAngle)
    {
        double field = (pose.FieldAngle - areaAngle) * Deg2Rad;          // the scope's field relative to the area's axes
        double c = Math.Cos(field), s = Math.Sin(field);
        var result = new Footprint[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            double ox = f.OffsetEast * c + f.OffsetNorth * s, oy = -f.OffsetEast * s + f.OffsetNorth * c;
            result[i] = new Footprint(pose.X + ox, pose.Y + oy, f.HalfWidth, f.HalfHeight, field + f.Rotation * Deg2Rad, f.Mask);
        }
        return result;
    }

    private void Bounds(in Footprint f, out int c0, out int c1, out int r0, out int r1)
    {
        double ex = Math.Abs(f.HalfWidth * Math.Cos(f.Theta)) + Math.Abs(f.HalfHeight * Math.Sin(f.Theta));
        double ey = Math.Abs(f.HalfWidth * Math.Sin(f.Theta)) + Math.Abs(f.HalfHeight * Math.Cos(f.Theta));
        c0 = Math.Max(0, (int)Math.Floor((f.Cx - ex + FovWidth / 2) / CellWidth));
        c1 = Math.Min(Cols - 1, (int)Math.Floor((f.Cx + ex + FovWidth / 2) / CellWidth));
        r0 = Math.Max(0, (int)Math.Floor((FovHeight / 2 - (f.Cy + ey)) / CellHeight));
        r1 = Math.Min(Rows - 1, (int)Math.Floor((FovHeight / 2 - (f.Cy - ey)) / CellHeight));
    }

    private bool Inside(in Footprint f, double cosT, double sinT, int col, int row)
    {
        double x = (col + 0.5) * CellWidth - FovWidth / 2 - f.Cx, y = FovHeight / 2 - (row + 0.5) * CellHeight - f.Cy;
        double lx = x * cosT - y * sinT, ly = x * sinT + y * cosT;      // into the frame's own axes
        return Math.Abs(lx) <= f.HalfWidth && Math.Abs(ly) <= f.HalfHeight;
    }

    /// <summary>Adds exposure to every cell the footprints cover, in each layer the footprint feeds.</summary>
    public void Paint(IReadOnlyList<Footprint> footprints, double seconds)
    {
        foreach (var f in footprints)
        {
            if (f.Mask == 0) continue;
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r))
                        for (int l = 0; l < Layers; l++)
                            if ((f.Mask >> l & 1) != 0) Planes[l][r * Cols + c] += (float)seconds;
        }
    }

    /// <summary>Sum of weight(seconds) over the cells the footprints cover: how much a shot there would help.</summary>
    public double Gain(IReadOnlyList<Footprint> footprints, Func<float, double> weight, int layer = 0)
    {
        double total = 0;
        var seconds = Planes[layer];
        foreach (var f in footprints)
        {
            if ((f.Mask >> layer & 1) == 0) continue;
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r)) total += weight(seconds[r * Cols + c]);
        }
        return total;
    }

    /// <summary>For a target depth: the squared deficit the footprints would fill (D), and how many already-finished cells they
    /// would expose again (W).</summary>
    public void DeficitAndWaste(IReadOnlyList<Footprint> footprints, double target, out double deficit, out int waste, int layer = 0)
    {
        deficit = 0; waste = 0;
        var seconds = Planes[layer];
        foreach (var f in footprints)
        {
            if ((f.Mask >> layer & 1) == 0) continue;
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r))
                    {
                        float v = seconds[r * Cols + c];
                        if (v >= target) waste++;
                        else { double d = 1 - v / target; deficit += d * d; }
                    }
        }
    }

    public double Min(int layer = 0) { float m = float.MaxValue; foreach (var v in Planes[layer]) if (v < m) m = v; return m; }
    public double Max(int layer = 0) { float m = 0; foreach (var v in Planes[layer]) if (v > m) m = v; return m; }
    public double Mean(int layer = 0) { double s = 0; foreach (var v in Planes[layer]) s += v; return s / Planes[layer].Length; }

    /// <summary>One byte per cell for display, scaled so 255 = <paramref name="full"/> seconds, reduced to at most the given size.</summary>
    public byte[] Render(double full, int maxCols, int maxRows, out int cols, out int rows, int layer = 0) =>
        RenderValues(Planes[layer], full, maxCols, maxRows, out cols, out rows);

    /// <summary>One byte per cell for display of how far the layers have got: each cell is its least advanced layer's seconds as a
    /// fraction of that layer's depth (<paramref name="targets"/>, one per layer; a layer without one counts against its best cell),
    /// 255 = all layers at their depth.</summary>
    public byte[] RenderProgress(IReadOnlyList<double> targets, int maxCols, int maxRows, out int cols, out int rows)
    {
        var worst = new float[Cols * Rows];
        Array.Fill(worst, float.MaxValue);
        for (int l = 0; l < Layers; l++)
        {
            double full = targets[l] > 0 ? targets[l] : Math.Max(Max(l), 1e-9);
            for (int i = 0; i < worst.Length; i++) worst[i] = Math.Min(worst[i], (float)(Planes[l][i] / full));
        }
        return RenderValues(worst, 1, maxCols, maxRows, out cols, out rows);
    }

    private byte[] RenderValues(float[] values, double full, int maxCols, int maxRows, out int cols, out int rows)
    {
        int bx = Math.Max(1, (int)Math.Ceiling(Cols / (double)maxCols)), by = Math.Max(1, (int)Math.Ceiling(Rows / (double)maxRows));
        cols = (Cols + bx - 1) / bx; rows = (Rows + by - 1) / by;
        var bytes = new byte[cols * rows];
        double scale = full > 0 ? 255.0 / full : 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double sum = 0; int n = 0;
                for (int y = r * by; y < Math.Min(Rows, (r + 1) * by); y++)
                    for (int x = c * bx; x < Math.Min(Cols, (c + 1) * bx); x++) { sum += values[y * Cols + x]; n++; }
                bytes[r * cols + c] = (byte)Math.Clamp(Math.Round(sum / Math.Max(n, 1) * scale), 0, 255);
            }
        return bytes;
    }
}
