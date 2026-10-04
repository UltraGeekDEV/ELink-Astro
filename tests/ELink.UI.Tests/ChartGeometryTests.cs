using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ELink.Core.Astro;
using ELink.UI.Controls;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>Large frames on the chart: the sky is curved, the chart's projection bends straight lines, and a picture laid on it
/// cannot be placed with one flat map. These pin down how well the frame, its edges and rasters follow.</summary>
public class ChartGeometryTests
{
    private static (double Ra, double Dec) Corner(double ra, double dec, double pa, double x, double y) => PlanProjection.ToSky(ra, dec, pa, x, y);

    [Fact]
    public void GreatCirclePiecesStayOnTheCircle()
    {
        var a = (RaHours: 3.0, DecDegrees: 60.0); var b = (RaHours: 9.0, DecDegrees: 55.0);
        var edge = ChartGeometry.GreatCircle(a, b, 2);
        Assert.True(edge.Count > 20);
        double total = Sky.SeparationDegrees(a.RaHours, a.DecDegrees, b.RaHours, b.DecDegrees), walked = 0;
        for (int i = 1; i < edge.Count; i++) walked += Sky.SeparationDegrees(edge[i - 1].RaHours, edge[i - 1].DecDegrees, edge[i].RaHours, edge[i].DecDegrees);
        Assert.Equal(total, walked, 6);                                  // a straight walk: no detours
        foreach (var p in edge) Assert.True(Math.Abs(Sky.SeparationDegrees(a.RaHours, a.DecDegrees, p.RaHours, p.DecDegrees) + Sky.SeparationDegrees(p.RaHours, p.DecDegrees, b.RaHours, b.DecDegrees) - total) < 1e-6);
    }

    [Theory]
    [InlineData(5.6, 0.0, 0.0, 60.0, 40.0, 160.0)]
    [InlineData(12.0, 60.0, 20.0, 60.0, 40.0, 150.0)]
    [InlineData(3.0, 85.0, 0.0, 50.0, 30.0, 140.0)]
    public void ALargeFramesEdgesAreCurvesOnTheChartAndStayOnTheTangentPlaneLines(double ra, double dec, double pa, double w, double h, double fov)
    {
        var proj = new SkyProjection(ra, dec, fov, 900, 650);
        var corners = new[] { Corner(ra, dec, pa, -w / 2, h / 2), Corner(ra, dec, pa, w / 2, h / 2), Corner(ra, dec, pa, w / 2, -h / 2), Corner(ra, dec, pa, -w / 2, -h / 2) };
        var outline = ChartGeometry.Outline(proj, corners.Select(c => (c.Ra, c.Dec)).ToList())!;
        Assert.NotNull(outline);
        Assert.True(outline.Count >= 4 * 12, $"{outline.Count} points: the edges are cut into pieces");
        // the top edge's middle is far from the straight chord of its corners: it is drawn curved
        proj.TryProject(corners[0].Ra, corners[0].Dec, out var x0, out var y0); proj.TryProject(corners[1].Ra, corners[1].Dec, out var x1, out var y1);
        var mid = Corner(ra, dec, pa, 0, h / 2); proj.TryProject(mid.Ra, mid.Dec, out var mx, out var my);
        double chord = Math.Abs((x1 - x0) * (y0 - my) - (x0 - mx) * (y1 - y0)) / Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        Assert.True(chord > 3, $"the middle of the edge is {chord:0.0} px off the straight chord at FOV {fov}°");
        // and that middle is the point of the edge's great circle halfway between the corners' tangent positions: on the edge polyline
        double nearest = double.MaxValue;
        for (int i = 0; i < outline.Count; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, t = Math.Clamp(((mx - a.X) * dx + (my - a.Y) * dy) / Math.Max(1e-9, dx * dx + dy * dy), 0, 1);
            nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(a.X + t * dx - mx, 2) + Math.Pow(a.Y + t * dy - my, 2)));
        }
        Assert.True(nearest < 0.5, $"the plan's edge midpoint is {nearest:0.00} px from the drawn outline");
    }

    private static Func<double, double, (double, double)> Plan(double ra, double dec, double pa, double w, double h, double pw, double ph) =>
        (px, py) => PlanProjection.ToSky(ra, dec, pa, (px / pw - 0.5) * w, (0.5 - py / ph) * h);

    [Theory]
    [InlineData(5.6, 0.0, 0.0, 60.0, 40.0, 160.0)]
    [InlineData(12.0, 60.0, 37.0, 60.0, 40.0, 150.0)]
    [InlineData(20.0, -45.0, 0.0, 30.0, 20.0, 80.0)]
    public void ARasterIsPlacedWithinAPixelOrTwoWhereTheSkyMappingSaysItIs(double ra, double dec, double pa, double w, double h, double fov)
    {
        var proj = new SkyProjection(ra, dec, fov, 900, 650);
        double pw = 240, ph = 160;
        var toSky = Plan(ra, dec, pa, w, h, pw, ph);
        var tiles = ChartGeometry.Tiles(proj, toSky, pw, ph);
        Assert.True(tiles.Count > 4, $"{tiles.Count} tiles");
        var rnd = new Random(7);
        double worst = 0;
        for (int k = 0; k < 400; k++)
        {
            double px = rnd.NextDouble() * pw, py = rnd.NextDouble() * ph;
            var (r, d) = toSky(px, py);
            Assert.True(proj.TryProject(r, d, out var ex, out var ey));
            var (src, map) = tiles.First(t => t.Source.Contains(new Point(px, py)));
            var m = map.Transform(new Point(px, py));
            worst = Math.Max(worst, Math.Sqrt((m.X - ex) * (m.X - ex) + (m.Y - ey) * (m.Y - ey)));
        }
        Assert.True(worst < 2.0, $"worst placement error {worst:0.00} px with {tiles.Count} tiles");

        // one flat map for the whole raster (what a three-corner map does) is far off for a large one
        var one = ChartGeometry.Tiles(proj, toSky, pw, ph, tiles: 1).Single();
        double oneWorst = 0;
        for (int k = 0; k < 204; k++)
        {
            // the far corner and the middle are where one map is worst
            double px = k == 200 ? pw : k == 201 ? pw / 2 : k == 202 ? pw : 0 + (k == 203 ? 0 : rnd.NextDouble() * pw), py = k == 200 ? ph : k == 201 ? ph / 2 : k == 202 ? 0 : k == 203 ? ph : rnd.NextDouble() * ph;
            var (r, d) = toSky(px, py); proj.TryProject(r, d, out var ex, out var ey);
            var m = one.Map.Transform(new Point(px, py));
            oneWorst = Math.Max(oneWorst, Math.Sqrt((m.X - ex) * (m.X - ex) + (m.Y - ey) * (m.Y - ey)));
        }
        if (w >= 60) Assert.True(oneWorst > Math.Max(3, 2 * worst), $"a single flat map would be off by {oneWorst:0.0} px against {worst:0.00} px tiled: tiling matters");
    }

    [Fact]
    public void SmallRastersNeedOneOrFewTiles()
    {
        Assert.Equal(1, ChartGeometry.TileCount(Plan(5.6, -5, 0, 1.3, 1.0, 100, 80), 100, 80));
        Assert.True(ChartGeometry.TileCount(Plan(5.6, -5, 0, 60, 40, 100, 80), 100, 80) >= 10);
        Assert.True(ChartGeometry.TileCount(Plan(5.6, -5, 0, 170, 150, 100, 80), 100, 80) <= 24);
    }

    [AvaloniaFact]
    public void AWideFrameIsDrawnCurvedAndCanBeDraggedTurnedAndResizedNearThePole()
    {
        var edits = new List<FrameEdit>();
        var chart = new SkyChart
        {
            CenterRa = 6, CenterDec = 80, Fov = 140, StarLimit = 6, ShowGrid = true, ShowConstellations = false,
            Frame = new ChartFrame(6, 80, 50, 30, 0, "wide", true),
            FrameEditedCommand = new RelayAction<FrameEdit>(e => edits.Add(e)),
        };
        var window = new Window { Width = 900, Height = 650, Content = chart };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(UiRig.ShotDir(), "frame-wide-pole.png"));
        var proj = chart.Projection;

        // inside: a drag moves the centre (no NaN, no jump to the far side)
        proj.TryProject(6, 80, out var cx, out var cy);
        var start = chart.TranslatePoint(new Point(cx, cy), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(30, 20)); window.MouseMove(start + new Vector(70, 40)); window.MouseUp(start + new Vector(70, 40), MouseButton.Left);
        Assert.NotEmpty(edits);
        foreach (var e in edits) { Assert.False(double.IsNaN(e.RaHours) || double.IsNaN(e.DecDegrees)); Assert.InRange(e.DecDegrees, -90, 90); Assert.Equal(50, e.WidthDegrees, 6); }
        var moved = edits[^1];
        // it followed the pointer: the grabbed point (the old centre) is now close to where the pointer let go
        proj.TryProject(moved.RaHours, moved.DecDegrees, out var nx, out var ny);
        Assert.True(Math.Abs(nx - (cx + 70)) < 6 && Math.Abs(ny - (cy + 40)) < 6, $"centre went to ({nx:0},{ny:0}), the pointer let go at ({cx + 70:0},{cy + 40:0})");
    }
}

/// <summary>A command that runs a delegate.</summary>
internal sealed class RelayAction<T>(Action<T> run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run((T)parameter!);
}
