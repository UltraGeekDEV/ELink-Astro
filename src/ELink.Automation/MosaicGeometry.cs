using ELink.Contracts.Automation;
using ELink.Core.Astro;

namespace ELink.Automation;

/// <summary>Turns a virtual field of view into panels, and orders them for a scan that never makes a long jump.</summary>
public static class MosaicGeometry
{
    public const int MaxPanels = 4096;
    private const double D2R = Math.PI / 180.0;

    /// <summary>Tiles the request. The panel step is frame × (1 − overlap); the grid is just big enough to cover the
    /// field and is centred on it. Returns a layout whose Error is set when the request makes no sense.</summary>
    public static MosaicLayout Plan(MosaicRequest r)
    {
        var layout = new MosaicLayout();
        string? error = Validate(r);
        if (error is not null) { layout.Error = error; return layout; }

        double fw = r.FovWidthDegrees.Value, fh = r.FovHeightDegrees.Value;
        double cw = r.FrameWidthDegrees.Value, ch = r.FrameHeightDegrees.Value, overlap = r.Overlap.Value;
        double stepX = cw * (1 - overlap), stepY = ch * (1 - overlap);
        int cols = Count(fw, cw, stepX), rows = Count(fh, ch, stepY);
        if ((long)rows * cols > MaxPanels) { layout.Error = $"that is {rows * cols} panels, more than the {MaxPanels} supported: use a bigger frame or a smaller area"; return layout; }

        double pa = r.PositionAngleDegrees.Value * D2R, cosPa = Math.Cos(pa), sinPa = Math.Sin(pa);
        double ra0 = r.Center.RaHours.Value, dec0 = r.Center.DecDegrees.Value;
        layout.Rows = rows; layout.Cols = cols; layout.StepXDegrees = stepX; layout.StepYDegrees = stepY;
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                double x = (col - (cols - 1) / 2.0) * stepX;        // east in the mosaic's own frame
                double y = ((rows - 1) / 2.0 - row) * stepY;        // north in the mosaic's own frame
                double east = x * cosPa + y * sinPa, north = -x * sinPa + y * cosPa;
                var (ra, dec) = Gnomonic.ToSky(ra0, dec0, east, north);
                layout.Panels.Add(new MosaicPanel { Row = row, Col = col, RaHours = ra, DecDegrees = dec });
            }
        return layout;
    }

    private static int Count(double fov, double frame, double step) => fov <= frame ? 1 : (int)Math.Ceiling((fov - frame) / step - 1e-9) + 1;

    public static string? Validate(MosaicRequest r)
    {
        if (!(r.FovWidthDegrees.Value > 0) || !(r.FovHeightDegrees.Value > 0)) return "the field of view must be larger than zero";
        if (!(r.FrameWidthDegrees.Value > 0) || !(r.FrameHeightDegrees.Value > 0)) return "the frame size must be larger than zero";
        if (!(r.Overlap.Value >= 0 && r.Overlap.Value < 0.9)) return "overlap must be from 0 up to 0.9";
        if (r.Center.Epoch.Text != "J2000") return "the mosaic centre must be given in J2000";
        double ra = r.Center.RaHours.Value, dec = r.Center.DecDegrees.Value;
        if (double.IsNaN(ra) || ra < 0 || ra >= 24) return "the centre RA must be 0..24 hours";
        if (double.IsNaN(dec) || dec < -90 || dec > 90) return "the centre Dec must be -90..90 degrees";
        if (Math.Abs(dec) + Math.Max(r.FovHeightDegrees.Value, r.FovWidthDegrees.Value) / 2 > 89.5) return "the area reaches a celestial pole, where the tiling is undefined";
        if (r.Passes.Value < 0) return "Passes cannot be negative";
        return null;
    }

    /// <summary>Serpentine scan order: rows alternate direction, and every other pass runs the whole route backwards, so
    /// the last panel of one pass is the first of the next and consecutive visits are always neighbours.</summary>
    public static IEnumerable<(int Row, int Col)> ScanOrder(int rows, int cols, int pass)
    {
        var forward = new List<(int, int)>(rows * cols);
        for (int r = 0; r < rows; r++)
            if (r % 2 == 0) for (int c = 0; c < cols; c++) forward.Add((r, c));
            else for (int c = cols - 1; c >= 0; c--) forward.Add((r, c));
        if (pass % 2 == 0) forward.Reverse();
        return forward;
    }
}
