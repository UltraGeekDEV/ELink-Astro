namespace ELink.Core.Astro;

/// <summary>Where the scope is, in the painted area's own frame: degrees from its centre (x along the area's width,
/// y along its height), plus the sky position angle the scope's field is turned to.</summary>
public readonly record struct Pose(double X, double Y, double FieldAngle);

/// <summary>One frame a visit takes: half sizes, how it is turned on the scope, and where it looks (scope-axis frame).</summary>
public readonly record struct FrameSpec(double HalfWidth, double HalfHeight, double Rotation, double OffsetEast, double OffsetNorth);

/// <summary>A rotated rectangle in the area's frame.</summary>
public readonly record struct Footprint(double Cx, double Cy, double HalfWidth, double HalfHeight, double Theta);

/// <summary>Exposure time received by every spot of the area, summed over all frames. The area is a grid of small cells;
/// a shot adds its exposure to each cell its frames' footprints cover.</summary>
public sealed class CoverageMap
{
    public const double Deg2Rad = Math.PI / 180.0;
    public int Cols { get; }
    public int Rows { get; }
    public double FovWidth { get; }
    public double FovHeight { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    public float[] Seconds { get; }

    public CoverageMap(double fovWidth, double fovHeight, double cell)
    {
        FovWidth = fovWidth; FovHeight = fovHeight;
        Cols = Math.Max(1, (int)Math.Round(fovWidth / cell));
        Rows = Math.Max(1, (int)Math.Round(fovHeight / cell));
        CellWidth = fovWidth / Cols; CellHeight = fovHeight / Rows;
        Seconds = new float[Cols * Rows];
    }

    private CoverageMap(CoverageMap other)
    {
        FovWidth = other.FovWidth; FovHeight = other.FovHeight; Cols = other.Cols; Rows = other.Rows;
        CellWidth = other.CellWidth; CellHeight = other.CellHeight;
        Seconds = (float[])other.Seconds.Clone();
    }

    /// <summary>A map restored from saved numbers (the grid exactly as it was).</summary>
    public static CoverageMap Restore(double fovWidth, double fovHeight, int cols, int rows, float[] seconds)
    {
        if (seconds.Length != cols * rows) throw new ArgumentException("seconds do not fit the grid");
        var m = new CoverageMap(fovWidth, fovHeight, fovWidth / cols);
        if (m.Cols != cols || m.Rows != rows) m = new CoverageMap(new CoverageMap(fovWidth, fovHeight, fovWidth / cols), cols, rows);
        Array.Copy(seconds, m.Seconds, seconds.Length);
        return m;
    }

    private CoverageMap(CoverageMap shape, int cols, int rows)
    {
        FovWidth = shape.FovWidth; FovHeight = shape.FovHeight; Cols = cols; Rows = rows;
        CellWidth = FovWidth / cols; CellHeight = FovHeight / rows;
        Seconds = new float[cols * rows];
    }

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
            result[i] = new Footprint(pose.X + ox, pose.Y + oy, f.HalfWidth, f.HalfHeight, field + f.Rotation * Deg2Rad);
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

    /// <summary>Adds exposure to every cell the footprints cover.</summary>
    public void Paint(IReadOnlyList<Footprint> footprints, double seconds)
    {
        foreach (var f in footprints)
        {
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r)) Seconds[r * Cols + c] += (float)seconds;
        }
    }

    /// <summary>Sum of weight(seconds) over the cells the footprints cover: how much a shot there would help.</summary>
    public double Gain(IReadOnlyList<Footprint> footprints, Func<float, double> weight)
    {
        double total = 0;
        foreach (var f in footprints)
        {
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r)) total += weight(Seconds[r * Cols + c]);
        }
        return total;
    }

    /// <summary>For a target depth: the squared deficit the footprints would fill (D), and how many already-finished cells they
    /// would expose again (W).</summary>
    public void DeficitAndWaste(IReadOnlyList<Footprint> footprints, double target, out double deficit, out int waste)
    {
        deficit = 0; waste = 0;
        foreach (var f in footprints)
        {
            Bounds(f, out int c0, out int c1, out int r0, out int r1);
            double cosT = Math.Cos(f.Theta), sinT = Math.Sin(f.Theta);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (Inside(f, cosT, sinT, c, r))
                    {
                        float v = Seconds[r * Cols + c];
                        if (v >= target) waste++;
                        else { double d = 1 - v / target; deficit += d * d; }
                    }
        }
    }

    public double Min() { float m = float.MaxValue; foreach (var v in Seconds) if (v < m) m = v; return m; }
    public double Max() { float m = 0; foreach (var v in Seconds) if (v > m) m = v; return m; }
    public double Mean() { double s = 0; foreach (var v in Seconds) s += v; return s / Seconds.Length; }

    /// <summary>One byte per cell for display, scaled so 255 = <paramref name="full"/> seconds, reduced to at most the given size.</summary>
    public byte[] Render(double full, int maxCols, int maxRows, out int cols, out int rows)
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
                    for (int x = c * bx; x < Math.Min(Cols, (c + 1) * bx); x++) { sum += Seconds[y * Cols + x]; n++; }
                bytes[r * cols + c] = (byte)Math.Clamp(Math.Round(sum / Math.Max(n, 1) * scale), 0, 255);
            }
        return bytes;
    }
}
