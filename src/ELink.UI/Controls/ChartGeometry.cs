using Avalonia;
using ELink.Core.Astro;

namespace ELink.UI.Controls;

/// <summary>The geometry behind drawing the image frame, fields and rasters on a stereographic chart. A chart draws what is
/// straight on the sky (the edges of a camera's field or of an image area: great circles) as curves, and a raster laid on it
/// cannot be placed with one affine map once it is larger than a few degrees: the helpers here cut edges into short pieces
/// and rasters into tiles, each as exact as a single map can be.</summary>
public static class ChartGeometry
{
    private const double D2R = Math.PI / 180;

    private static (double X, double Y, double Z) Vector(double raHours, double decDegrees)
    {
        double a = raHours * 15 * D2R, d = decDegrees * D2R;
        return (Math.Cos(d) * Math.Cos(a), Math.Cos(d) * Math.Sin(a), Math.Sin(d));
    }

    private static (double RaHours, double DecDegrees) ToRaDec((double X, double Y, double Z) v)
    {
        double ra = Math.Atan2(v.Y, v.X) / D2R / 15, dec = Math.Atan2(v.Z, Math.Sqrt(v.X * v.X + v.Y * v.Y)) / D2R;
        return (Precession.NormalizeHours(ra), dec);
    }

    /// <summary>Points along the great circle from one sky position to another (both ends included): the straight line between
    /// them on a tangent plane. Pieces are about <paramref name="stepDegrees"/> long.</summary>
    public static IReadOnlyList<(double RaHours, double DecDegrees)> GreatCircle((double RaHours, double DecDegrees) from, (double RaHours, double DecDegrees) to, double stepDegrees = 2, int maxPieces = 64)
    {
        var a = Vector(from.RaHours, from.DecDegrees); var b = Vector(to.RaHours, to.DecDegrees);
        double dot = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1), omega = Math.Acos(dot);
        int pieces = Math.Clamp((int)Math.Ceiling(omega / D2R / Math.Max(0.01, stepDegrees)), 1, maxPieces);
        var list = new List<(double, double)>(pieces + 1) { from };
        if (omega < 1e-9) return list;
        double sinO = Math.Sin(omega);
        for (int i = 1; i < pieces; i++)
        {
            double t = (double)i / pieces, wa = Math.Sin((1 - t) * omega) / sinO, wb = Math.Sin(t * omega) / sinO;
            list.Add(ToRaDec((wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z)));
        }
        list.Add(to);
        return list;
    }

    /// <summary>A closed outline through the corners with every edge a great circle, as screen points; null when part of it cannot be
    /// drawn (on the far side of the sky).</summary>
    public static List<Point>? Outline(SkyProjection proj, IReadOnlyList<(double RaHours, double DecDegrees)> corners, double stepDegrees = 2)
    {
        var pts = new List<Point>();
        for (int i = 0; i < corners.Count; i++)
        {
            var edge = GreatCircle(corners[i], corners[(i + 1) % corners.Count], stepDegrees);
            for (int k = 0; k < edge.Count - 1; k++)    // the last point is the next edge's first
            {
                if (!proj.TryProject(edge[k].RaHours, edge[k].DecDegrees, out var x, out var y)) return null;
                pts.Add(new Point(x, y));
            }
        }
        return pts;
    }

    /// <summary>Whether a point lies inside a polygon (any shape).</summary>
    public static bool Contains(IReadOnlyList<Point> polygon, Point p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>How many tiles per side a raster needs so that each tile can be drawn with one affine map: about every 4° of sky.</summary>
    public static int TileCount(Func<double, double, (double RaHours, double DecDegrees)> pixelToSky, double width, double height, double tileDegrees = 4)
    {
        var corners = new[] { pixelToSky(0, 0), pixelToSky(width, 0), pixelToSky(0, height), pixelToSky(width, height), pixelToSky(width / 2, height / 2) };
        double most = 0;
        for (int i = 0; i < corners.Length; i++)
            for (int j = i + 1; j < corners.Length; j++) most = Math.Max(most, Sky.SeparationDegrees(corners[i].RaHours, corners[i].DecDegrees, corners[j].RaHours, corners[j].DecDegrees));
        return Math.Clamp((int)Math.Ceiling(most / Math.Max(0.1, tileDegrees)), 1, 24);
    }

    /// <summary>The pieces a raster is drawn in: for each tile the part of the picture (pixels) and the map that carries it to the
    /// chart. A tile whose corners are not all on the chart is left out.</summary>
    public static List<(Rect Source, Matrix Map)> Tiles(SkyProjection proj, Func<double, double, (double RaHours, double DecDegrees)> pixelToSky, double width, double height, int? tiles = null)
    {
        int n = tiles ?? TileCount(pixelToSky, width, height);
        var list = new List<(Rect, Matrix)>();
        double tw = width / n, th = height / n;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double x0 = i * tw, y0 = j * th;
                var a = pixelToSky(x0, y0); var b = pixelToSky(x0 + tw, y0); var c = pixelToSky(x0, y0 + th);
                if (!proj.TryProject(a.RaHours, a.DecDegrees, out var ax, out var ay) || !proj.TryProject(b.RaHours, b.DecDegrees, out var bx, out var by)
                    || !proj.TryProject(c.RaHours, c.DecDegrees, out var cx, out var cy)) continue;
                // the map takes picture pixels to the chart: x0,y0 -> a, x0+tw,y0 -> b, x0,y0+th -> c
                double m11 = (bx - ax) / tw, m12 = (by - ay) / tw, m21 = (cx - ax) / th, m22 = (cy - ay) / th;
                var m = new Matrix(m11, m12, m21, m22, ax - m11 * x0 - m21 * y0, ay - m12 * x0 - m22 * y0);
                list.Add((new Rect(x0, y0, tw, th), m));
            }
        return list;
    }
}
