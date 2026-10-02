using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ELink.Core.Astro;

namespace ELink.UI.Controls;

public sealed record ChartStar(float RaHours, float DecDegrees, float Magnitude, float ColorIndex, string Label);
public sealed record ChartDso(string Id, string Kind, string CommonName, float RaHours, float DecDegrees, float Magnitude, float MajorArcmin, float MinorArcmin, float PositionAngle);
public sealed record ChartSegment(float Ra1Hours, float Dec1Degrees, float Ra2Hours, float Dec2Degrees);
public sealed record ChartLabel(string Text, float RaHours, float DecDegrees);
/// <summary>A reticle (mount, scope) or the selection ring.</summary>
public sealed record ChartMarker(double RaHours, double DecDegrees, string Label, Color Color, bool IsSelection = false);
/// <summary>An outline on the sky, e.g. a mosaic area or frame footprints.</summary>
/// <summary>The local horizon as a line on the sky (J2000), with N/E/S/W labels.</summary>
public sealed record ChartHorizon(IReadOnlyList<(double RaHours, double DecDegrees)> Line, IReadOnlyList<ChartLabel> Cardinals);
/// <summary>Sun, Moon or a planet: a disc with a name.</summary>
public sealed record ChartBody(double RaHours, double DecDegrees, string Label, Color Color, double Radius);
public sealed record ChartPolygon(IReadOnlyList<(double RaHours, double DecDegrees)> Corners, Color Color, string Label = "");

/// <summary>An interactive sky chart: drag to pan, wheel to zoom, click to pick. It only draws what it is given (stars, deep-sky
/// objects, constellation figures, markers, outlines); fetching from the atlas is the view model's job.</summary>
public sealed class SkyChart : Control
{
    public static readonly StyledProperty<double> CenterRaProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(CenterRa), 5.6, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> CenterDecProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(CenterDec), 0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> FovProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(Fov), 60, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> StarLimitProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(StarLimit), 6.5);
    public static readonly StyledProperty<IReadOnlyList<ChartStar>?> StarsProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartStar>?>(nameof(Stars));
    public static readonly StyledProperty<IReadOnlyList<ChartDso>?> DsosProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartDso>?>(nameof(Dsos));
    public static readonly StyledProperty<IReadOnlyList<ChartSegment>?> LinesProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartSegment>?>(nameof(Lines));
    public static readonly StyledProperty<IReadOnlyList<ChartLabel>?> LabelsProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartLabel>?>(nameof(Labels));
    public static readonly StyledProperty<IReadOnlyList<ChartMarker>?> MarkersProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartMarker>?>(nameof(Markers));
    public static readonly StyledProperty<ChartHorizon?> HorizonProperty = AvaloniaProperty.Register<SkyChart, ChartHorizon?>(nameof(Horizon));
    public static readonly StyledProperty<IReadOnlyList<ChartBody>?> BodiesProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartBody>?>(nameof(Bodies));
    public static readonly StyledProperty<IReadOnlyList<ChartPolygon>?> PolygonsProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartPolygon>?>(nameof(Polygons));
    public static readonly StyledProperty<ICommand?> PickCommandProperty = AvaloniaProperty.Register<SkyChart, ICommand?>(nameof(PickCommand));
    public static readonly StyledProperty<bool> ShowGridProperty = AvaloniaProperty.Register<SkyChart, bool>(nameof(ShowGrid), true);
    public static readonly StyledProperty<bool> ShowConstellationsProperty = AvaloniaProperty.Register<SkyChart, bool>(nameof(ShowConstellations), true);

    public double CenterRa { get => GetValue(CenterRaProperty); set => SetValue(CenterRaProperty, value); }
    public double CenterDec { get => GetValue(CenterDecProperty); set => SetValue(CenterDecProperty, value); }
    public double Fov { get => GetValue(FovProperty); set => SetValue(FovProperty, value); }
    public double StarLimit { get => GetValue(StarLimitProperty); set => SetValue(StarLimitProperty, value); }
    public IReadOnlyList<ChartStar>? Stars { get => GetValue(StarsProperty); set => SetValue(StarsProperty, value); }
    public IReadOnlyList<ChartDso>? Dsos { get => GetValue(DsosProperty); set => SetValue(DsosProperty, value); }
    public IReadOnlyList<ChartSegment>? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public IReadOnlyList<ChartLabel>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public IReadOnlyList<ChartMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public IReadOnlyList<ChartPolygon>? Polygons { get => GetValue(PolygonsProperty); set => SetValue(PolygonsProperty, value); }
    public ChartHorizon? Horizon { get => GetValue(HorizonProperty); set => SetValue(HorizonProperty, value); }
    public IReadOnlyList<ChartBody>? Bodies { get => GetValue(BodiesProperty); set => SetValue(BodiesProperty, value); }
    /// <summary>Executed with a (RaHours, DecDegrees, PixelsPerDegree) tuple when the user clicks without dragging.</summary>
    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowConstellations { get => GetValue(ShowConstellationsProperty); set => SetValue(ShowConstellationsProperty, value); }

    static SkyChart()
    {
        AffectsRender<SkyChart>(CenterRaProperty, CenterDecProperty, FovProperty, StarsProperty, DsosProperty, LinesProperty, LabelsProperty,
            MarkersProperty, PolygonsProperty, HorizonProperty, BodiesProperty, ShowGridProperty, ShowConstellationsProperty, StarLimitProperty);
        FocusableProperty.OverrideDefaultValue<SkyChart>(true);
    }

    public SkyChart() { ClipToBounds = true; }

    public SkyProjection Projection => new(CenterRa, CenterDec, Fov, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));

    // ---- input ---------------------------------------------------------------------------------------------------

    private Point? _down; private SkyProjection _downProjection; private bool _dragged;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _down = e.GetPosition(this); _downProjection = Projection; _dragged = false;
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_down is not { } start) return;
        var now = e.GetPosition(this);
        double dx = now.X - start.X, dy = now.Y - start.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        // keep the sky point that was under the pointer under it
        var (ra, dec) = _downProjection.Unproject(_downProjection.Width / 2 - dx, _downProjection.Height / 2 - dy);
        CenterRa = ra; CenterDec = Math.Clamp(dec, -90, 90);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_down is not null && !_dragged)
        {
            var p = e.GetPosition(this);
            var proj = Projection;
            var (ra, dec) = proj.Unproject(p.X, p.Y);
            var arg = (ra, dec, proj.PixelsPerDegree);
            if (PickCommand?.CanExecute(arg) == true) PickCommand.Execute(arg);
        }
        _down = null; e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Fov = Math.Clamp(Fov * Math.Pow(0.85, e.Delta.Y), 0.1, 180);
        e.Handled = true;
    }

    // ---- drawing -------------------------------------------------------------------------------------------------

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(7, 11, 20));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 90, 120, 170)), 1);
    private static readonly IPen EquatorPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 120, 140, 200)), 1);
    private static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.FromArgb(150, 70, 130, 200)), 1.2);
    private static readonly IBrush ConstellationText = new SolidColorBrush(Color.FromArgb(150, 110, 150, 210));
    private static readonly IBrush StarText = new SolidColorBrush(Color.FromArgb(200, 210, 210, 220));
    private static readonly Typeface Face = new(FontFamily.Default);

    public override void Render(DrawingContext ctx)
    {
        var proj = Projection;
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        if (ShowGrid) DrawGrid(ctx, proj);
        if (ShowConstellations) DrawConstellations(ctx, proj);
        DrawDsos(ctx, proj);
        DrawStars(ctx, proj);
        DrawBodies(ctx, proj);
        DrawHorizon(ctx, proj);
        DrawPolygons(ctx, proj);
        DrawMarkers(ctx, proj);
        DrawScale(ctx, proj);
    }

    private bool Visible(double x, double y, double margin = 20) => x >= -margin && y >= -margin && x <= Bounds.Width + margin && y <= Bounds.Height + margin;

    private void Text(DrawingContext ctx, string text, double x, double y, IBrush brush, double size = 11)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
        ctx.DrawText(ft, new Point(x, y));
    }

    private void DrawGrid(DrawingContext ctx, SkyProjection proj)
    {
        double fov = Fov;
        double decStep = fov > 90 ? 30 : fov > 40 ? 15 : fov > 15 ? 10 : fov > 6 ? 5 : fov > 2 ? 1 : fov > 0.8 ? 0.5 : 0.1;
        double raStep = decStep / 15 * (Math.Abs(CenterDec) > 70 ? 3 : 1);   // hours
        for (double dec = -90 + decStep; dec < 90; dec += decStep)
            Polyline(ctx, proj, Math.Abs(dec) < 1e-9 ? EquatorPen : GridPen, Enumerable.Range(0, 361).Select(i => (i / 15.0, dec)));
        for (double ra = 0; ra < 24 - 1e-9; ra += raStep)
            Polyline(ctx, proj, GridPen, Enumerable.Range(0, 181).Select(i => (ra, -90.0 + i)));
    }

    private void Polyline(DrawingContext ctx, SkyProjection proj, IPen pen, IEnumerable<(double Ra, double Dec)> points)
    {
        Point? last = null;
        foreach (var (ra, dec) in points)
        {
            if (!proj.TryProject(ra, dec, out var x, out var y)) { last = null; continue; }
            var p = new Point(x, y);
            if (last is { } l && (Visible(l.X, l.Y, 2000) || Visible(x, y, 2000)) && Math.Abs(l.X - x) + Math.Abs(l.Y - y) < Bounds.Width) ctx.DrawLine(pen, l, p);
            last = p;
        }
    }

    private void DrawConstellations(DrawingContext ctx, SkyProjection proj)
    {
        if (Lines is { } lines)
            foreach (var s in lines)
            {
                if (!proj.TryProject(s.Ra1Hours, s.Dec1Degrees, out var x1, out var y1) || !proj.TryProject(s.Ra2Hours, s.Dec2Degrees, out var x2, out var y2)) continue;
                if (!Visible(x1, y1, 400) && !Visible(x2, y2, 400)) continue;
                if (Math.Abs(x1 - x2) + Math.Abs(y1 - y2) > Bounds.Width * 1.5) continue;
                ctx.DrawLine(LinePen, new Point(x1, y1), new Point(x2, y2));
            }
        if (Labels is { } labels && Fov > 8)
            foreach (var l in labels)
                if (proj.TryProject(l.RaHours, l.DecDegrees, out var x, out var y) && Visible(x, y)) Text(ctx, l.Text, x, y, ConstellationText, 12);
    }

    private static Color KindColor(string kind) => kind switch
    {
        "Galaxy" or "GalaxyCluster" => Color.FromRgb(230, 120, 120),
        "OpenCluster" or "Asterism" => Color.FromRgb(230, 210, 110),
        "GlobularCluster" => Color.FromRgb(230, 170, 90),
        "Nebula" or "SupernovaRemnant" => Color.FromRgb(110, 220, 160),
        "PlanetaryNebula" => Color.FromRgb(120, 200, 240),
        "DarkNebula" => Color.FromRgb(140, 140, 160),
        _ => Color.FromRgb(170, 170, 190),
    };

    private void DrawDsos(DrawingContext ctx, SkyProjection proj)
    {
        if (Dsos is not { } dsos) return;
        double ppd = proj.PixelsPerDegree;
        foreach (var d in dsos)
        {
            if (!proj.TryProject(d.RaHours, d.DecDegrees, out var x, out var y) || !Visible(x, y, 200)) continue;
            double a = Math.Max(3, d.MajorArcmin / 60 * ppd / 2), b = Math.Max(3, (d.MinorArcmin > 0 ? d.MinorArcmin : d.MajorArcmin) / 60 * ppd / 2);
            var color = KindColor(d.Kind);
            var pen = new Pen(new SolidColorBrush(color, 0.85), 1.2);
            // position angle: from north (up) toward east (left)
            using (ctx.PushTransform(Matrix.CreateRotation(-d.PositionAngle * Math.PI / 180) * Matrix.CreateTranslation(x, y)))
                ctx.DrawEllipse(null, pen, new Point(0, 0), b, a);
            bool label = Fov < 25 ? (a > 4 || (!float.IsNaN(d.Magnitude) && d.Magnitude < 11)) : !float.IsNaN(d.Magnitude) && d.Magnitude < 7.5;
            if (label) Text(ctx, d.Id, x + b + 2, y - 6, new SolidColorBrush(color, 0.9), 10);
        }
    }

    /// <summary>Star colour from its B-V index: blue-white through white and yellow to orange-red.</summary>
    public static Color StarColor(float bv)
    {
        double t = Math.Clamp((bv + 0.3) / 2.0, 0, 1);
        (double r, double g, double b) = t < 0.25 ? Lerp((170, 190, 255), (235, 240, 255), t / 0.25)
            : t < 0.5 ? Lerp((235, 240, 255), (255, 245, 220), (t - 0.25) / 0.25)
            : t < 0.75 ? Lerp((255, 245, 220), (255, 205, 140), (t - 0.5) / 0.25)
            : Lerp((255, 205, 140), (255, 150, 110), (t - 0.75) / 0.25);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static (double, double, double) Lerp((double, double, double) a, (double, double, double) b, double t) =>
        (a.Item1 + (b.Item1 - a.Item1) * t, a.Item2 + (b.Item2 - a.Item2) * t, a.Item3 + (b.Item3 - a.Item3) * t);

    /// <summary>Disc radius in pixels for a magnitude, relative to the faintest drawn.</summary>
    public static double StarRadius(float magnitude, double limit) => Math.Clamp(0.7 + (limit - magnitude) * 0.55, 0.7, 7);

    private void DrawStars(DrawingContext ctx, SkyProjection proj)
    {
        if (Stars is not { } stars) return;
        double limit = StarLimit;
        var brushes = new Dictionary<int, IBrush>();
        foreach (var s in stars)
        {
            if (!proj.TryProject(s.RaHours, s.DecDegrees, out var x, out var y) || !Visible(x, y, 10)) continue;
            int key = (int)Math.Round((s.ColorIndex + 0.3) * 10);
            if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = new SolidColorBrush(StarColor(s.ColorIndex));
            double r = StarRadius(s.Magnitude, limit);
            ctx.DrawEllipse(brush, null, new Point(x, y), r, r);
            if (s.Label != "" && (s.Magnitude < 2.2 || (Fov < 40 && s.Magnitude < 4.5) || Fov < 12)) Text(ctx, s.Label, x + r + 2, y - 6, StarText, 10);
        }
    }

    private static readonly IPen HorizonPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 120, 200, 90)), 2.2);
    private static readonly IBrush HorizonText = new SolidColorBrush(Color.FromArgb(230, 150, 220, 110));

    private void DrawHorizon(DrawingContext ctx, SkyProjection proj)
    {
        if (Horizon is not { } h) return;
        Polyline(ctx, proj, HorizonPen, h.Line);
        foreach (var l in h.Cardinals)
            if (proj.TryProject(l.RaHours, l.DecDegrees, out var x, out var y) && Visible(x, y)) Text(ctx, l.Text, x - 5, y - 18, HorizonText, 14);
    }

    private void DrawBodies(DrawingContext ctx, SkyProjection proj)
    {
        if (Bodies is not { } bodies) return;
        foreach (var b in bodies)
        {
            if (!proj.TryProject(b.RaHours, b.DecDegrees, out var x, out var y) || !Visible(x, y)) continue;
            // the true disc when zoomed in far enough, else a symbol
            double r = Math.Max(b.Radius * proj.PixelsPerDegree, 4.5);
            ctx.DrawEllipse(new SolidColorBrush(b.Color, 0.9), new Pen(Brushes.Black, 0.5), new Point(x, y), r, r);
            Text(ctx, b.Label, x + r + 3, y - 7, new SolidColorBrush(b.Color), 12);
        }
    }

    private void DrawPolygons(DrawingContext ctx, SkyProjection proj)
    {
        if (Polygons is not { } polys) return;
        foreach (var poly in polys)
        {
            var pen = new Pen(new SolidColorBrush(poly.Color), 1.5);
            var pts = new List<Point>();
            foreach (var (ra, dec) in poly.Corners) if (proj.TryProject(ra, dec, out var x, out var y)) pts.Add(new Point(x, y));
            if (pts.Count < 2) continue;
            for (int i = 0; i < pts.Count; i++) ctx.DrawLine(pen, pts[i], pts[(i + 1) % pts.Count]);
            if (poly.Label != "") Text(ctx, poly.Label, pts[0].X + 3, pts[0].Y + 2, new SolidColorBrush(poly.Color), 11);
        }
    }

    private void DrawMarkers(DrawingContext ctx, SkyProjection proj)
    {
        if (Markers is not { } markers) return;
        foreach (var m in markers)
        {
            if (!proj.TryProject(m.RaHours, m.DecDegrees, out var x, out var y) || !Visible(x, y)) continue;
            var pen = new Pen(new SolidColorBrush(m.Color), m.IsSelection ? 1.5 : 2);
            if (m.IsSelection) ctx.DrawEllipse(null, pen, new Point(x, y), 11, 11);
            else
            {
                ctx.DrawEllipse(null, pen, new Point(x, y), 14, 14);
                ctx.DrawLine(pen, new Point(x - 22, y), new Point(x - 7, y)); ctx.DrawLine(pen, new Point(x + 7, y), new Point(x + 22, y));
                ctx.DrawLine(pen, new Point(x, y - 22), new Point(x, y - 7)); ctx.DrawLine(pen, new Point(x, y + 7), new Point(x, y + 22));
            }
            if (m.Label != "") Text(ctx, m.Label, x + 16, y + 6, new SolidColorBrush(m.Color), 11);
        }
    }

    private void DrawScale(DrawingContext ctx, SkyProjection proj)
    {
        string fov = Fov >= 1 ? $"{Fov:0.#}°" : $"{Fov * 60:0}'";
        Text(ctx, $"FOV {fov}   centre {Sexagesimal.Format(CenterRa, 0)}  {Sexagesimal.Format(CenterDec, 0)}", 8, Bounds.Height - 20, StarText, 11);
    }
}
