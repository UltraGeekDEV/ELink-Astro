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
/// <summary>A raster laid on the sky: <c>PixelToSky</c> says where a picture pixel (x right, y down, from the top left corner) lies,
/// so any orientation, mirroring and size works (it is drawn in tiles, each placed exactly). Used for the coverage map and the stacked image.</summary>
public sealed record ChartImage(Avalonia.Media.IImage Bitmap, Func<double, double, (double RaHours, double DecDegrees)> PixelToSky, double Opacity);
/// <summary>The image being planned: a rectangle on the sky that can be moved, resized and turned with the mouse.
/// <see cref="AngleDegrees"/> is where its up points, east of north.</summary>
public sealed record ChartFrame(double RaHours, double DecDegrees, double WidthDegrees, double HeightDegrees, double AngleDegrees, string Label, bool Editable);
/// <summary>What the user made of the frame by dragging it.</summary>
public readonly record struct FrameEdit(double RaHours, double DecDegrees, double WidthDegrees, double HeightDegrees, double AngleDegrees);

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
    public static readonly StyledProperty<ChartFrame?> FrameProperty = AvaloniaProperty.Register<SkyChart, ChartFrame?>(nameof(Frame));
    public static readonly StyledProperty<IReadOnlyList<ChartImage>?> ImagesProperty = AvaloniaProperty.Register<SkyChart, IReadOnlyList<ChartImage>?>(nameof(Images));
    public static readonly StyledProperty<ICommand?> DoubleClickCommandProperty = AvaloniaProperty.Register<SkyChart, ICommand?>(nameof(DoubleClickCommand));
    public static readonly StyledProperty<ICommand?> FrameDoubleClickCommandProperty = AvaloniaProperty.Register<SkyChart, ICommand?>(nameof(FrameDoubleClickCommand));
    public static readonly StyledProperty<ICommand?> FrameEditedCommandProperty = AvaloniaProperty.Register<SkyChart, ICommand?>(nameof(FrameEditedCommand));
    public static readonly StyledProperty<double> ContextRaProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(ContextRa), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> ContextDecProperty = AvaloniaProperty.Register<SkyChart, double>(nameof(ContextDec), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<ICommand?> PickCommandProperty = AvaloniaProperty.Register<SkyChart, ICommand?>(nameof(PickCommand));
    public static readonly StyledProperty<bool> ShowGridProperty = AvaloniaProperty.Register<SkyChart, bool>(nameof(ShowGrid), true);
    public static readonly StyledProperty<bool> ShowConstellationsProperty = AvaloniaProperty.Register<SkyChart, bool>(nameof(ShowConstellations), true);
    public static readonly StyledProperty<SkyGlowMap?> SkyGlowProperty = AvaloniaProperty.Register<SkyChart, SkyGlowMap?>(nameof(SkyGlow));

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
    public ChartFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public IReadOnlyList<ChartImage>? Images { get => GetValue(ImagesProperty); set => SetValue(ImagesProperty, value); }
    /// <summary>Executed with a (RaHours, DecDegrees, PixelsPerDegree) tuple when the sky (not the frame) is double-clicked.</summary>
    public ICommand? DoubleClickCommand { get => GetValue(DoubleClickCommandProperty); set => SetValue(DoubleClickCommandProperty, value); }
    /// <summary>Executed (with no argument) when the frame itself is double-clicked.</summary>
    public ICommand? FrameDoubleClickCommand { get => GetValue(FrameDoubleClickCommandProperty); set => SetValue(FrameDoubleClickCommandProperty, value); }
    /// <summary>Executed with a <see cref="FrameEdit"/> every time the frame is dragged.</summary>
    public ICommand? FrameEditedCommand { get => GetValue(FrameEditedCommandProperty); set => SetValue(FrameEditedCommandProperty, value); }
    /// <summary>The sky position under the pointer when the context menu (right button) was opened.</summary>
    public double ContextRa { get => GetValue(ContextRaProperty); set => SetValue(ContextRaProperty, value); }
    public double ContextDec { get => GetValue(ContextDecProperty); set => SetValue(ContextDecProperty, value); }
    public ChartHorizon? Horizon { get => GetValue(HorizonProperty); set => SetValue(HorizonProperty, value); }
    public IReadOnlyList<ChartBody>? Bodies { get => GetValue(BodiesProperty); set => SetValue(BodiesProperty, value); }
    /// <summary>Executed with a (RaHours, DecDegrees, PixelsPerDegree) tuple when the user clicks without dragging.</summary>
    public ICommand? PickCommand { get => GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowConstellations { get => GetValue(ShowConstellationsProperty); set => SetValue(ShowConstellationsProperty, value); }
    /// <summary>The Milky Way's glow, drawn under everything else.</summary>
    public SkyGlowMap? SkyGlow { get => GetValue(SkyGlowProperty); set => SetValue(SkyGlowProperty, value); }

    static SkyChart()
    {
        AffectsRender<SkyChart>(CenterRaProperty, CenterDecProperty, FovProperty, StarsProperty, DsosProperty, LinesProperty, LabelsProperty,
            MarkersProperty, PolygonsProperty, HorizonProperty, BodiesProperty, ShowGridProperty, ShowConstellationsProperty, StarLimitProperty, FrameProperty, ImagesProperty, SkyGlowProperty);
        FocusableProperty.OverrideDefaultValue<SkyChart>(true);
    }

    public SkyChart()
    {
        ClipToBounds = true;
        DoubleTapped += (_, e) =>
        {
            var at = e.GetPosition(this);
            if (HitFrame(at) != Drag.None) { if (FrameDoubleClickCommand?.CanExecute(null) == true) FrameDoubleClickCommand.Execute(null); return; }
            var proj = Projection; var (ra, dec) = proj.Unproject(at.X, at.Y);
            var arg = (ra, dec, proj.PixelsPerDegree);
            if (DoubleClickCommand?.CanExecute(arg) == true) DoubleClickCommand.Execute(arg);
        };
    }

    public SkyProjection Projection => new(CenterRa, CenterDec, Fov, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));

    // ---- input ---------------------------------------------------------------------------------------------------

    private enum Drag { None, Pan, MoveFrame, ResizeFrame, RotateFrame }
    private Point? _down; private SkyProjection _downProjection; private bool _dragged;
    private Drag _drag;
    private (double Xi, double Eta) _grab;      // where in the frame a move started, as a tangent-plane offset from its centre
    private const double HandleRadius = 8;

    /// <summary>The frame's corners (top left, top right, bottom right, bottom left), its outline with edges as great circles, its
    /// centre and the rotation handle, on the screen. False when part of it is on the far side of the sky.</summary>
    private bool FrameGeometry(SkyProjection proj, out Point[] corners, out List<Point> outline, out Point centre, out Point handle)
    {
        corners = new Point[4]; outline = new(); centre = default; handle = default;
        if (Frame is not { } f || f.WidthDegrees <= 0 || f.HeightDegrees <= 0) return false;
        double w = Math.Min(f.WidthDegrees, 170) / 2, h = Math.Min(f.HeightDegrees, 170) / 2;
        (double Ra, double Dec) At(double x, double y) => PlanProjection.ToSky(f.RaHours, f.DecDegrees, f.AngleDegrees, x, y);
        var sky = new[] { At(-w, h), At(w, h), At(w, -h), At(-w, -h) };
        var o = ChartGeometry.Outline(proj, sky.Select(p => (p.Ra, p.Dec)).ToList());
        if (o is null) return false;
        outline = o;
        for (int k = 0; k < 4; k++) { proj.TryProject(sky[k].Ra, sky[k].Dec, out var cx, out var cy); corners[k] = new Point(cx, cy); }
        var mid = At(0, 0); var top = At(0, h);
        if (!proj.TryProject(mid.Ra, mid.Dec, out var mx, out var my) || !proj.TryProject(top.Ra, top.Dec, out var tx, out var ty)) return false;
        centre = new Point(mx, my);
        double dx = tx - mx, dy = ty - my, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        handle = new Point(tx + dx / len * 28, ty + dy / len * 28);
        return true;
    }

    private Drag HitFrame(Point at)
    {
        var proj = Projection;
        if (Frame is not { Editable: true } || !FrameGeometry(proj, out var c, out var outline, out _, out var handle)) return Drag.None;
        double D(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        if (D(at, handle) <= HandleRadius + 4) return Drag.RotateFrame;
        if (c.Any(p => D(at, p) <= HandleRadius + 3)) return Drag.ResizeFrame;
        return ChartGeometry.Contains(outline, at) ? Drag.MoveFrame : Drag.None;
    }

    private void EditFrame(Point pointer)
    {
        if (Frame is not { } f) return;
        var proj = Projection;
        var (ra, dec) = proj.Unproject(pointer.X, pointer.Y);
        FrameEdit edit = new(f.RaHours, f.DecDegrees, f.WidthDegrees, f.HeightDegrees, f.AngleDegrees);
        switch (_drag)
        {
            case Drag.MoveFrame:
            {
                // the centre that keeps the grabbed point of the frame (a tangent-plane offset from the centre) under the pointer
                var (cra, cdec) = Gnomonic.ToSkyPlane(ra, dec, -_grab.Xi, -_grab.Eta);
                for (int k = 0; k < 3; k++)
                {
                    var (gra, gdec) = Gnomonic.ToSkyPlane(cra, cdec, _grab.Xi, _grab.Eta);
                    if (!Gnomonic.TryFromSkyPlane(ra, dec, gra, gdec, out var rx, out var ry)) break;
                    (cra, cdec) = Gnomonic.ToSkyPlane(cra, cdec, -rx, -ry);
                }
                edit = edit with { RaHours = cra, DecDegrees = Math.Clamp(cdec, -90, 90) };
                break;
            }
            case Drag.ResizeFrame:
                if (!PlanProjection.TryFromSky(f.RaHours, f.DecDegrees, f.AngleDegrees, ra, dec, out var lx, out var ly)) return;
                edit = edit with { WidthDegrees = Math.Clamp(2 * Math.Abs(lx), 0.02, 170), HeightDegrees = Math.Clamp(2 * Math.Abs(ly), 0.02, 170) };
                break;
            case Drag.RotateFrame:
                if (!Gnomonic.TryFromSkyPlane(f.RaHours, f.DecDegrees, ra, dec, out var xi, out var eta)) return;
                double angle = Math.Atan2(xi, eta) * 180 / Math.PI;
                if (Math.Abs(angle) < 1.5) angle = 0;
                edit = edit with { AngleDegrees = Math.Round(angle, 1) };
                break;
        }
        if (FrameEditedCommand?.CanExecute(edit) == true) FrameEditedCommand.Execute(edit);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var at = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            // remember what is under the pointer for the context menu (which opens by itself)
            var (cra, cdec) = Projection.Unproject(at.X, at.Y);
            ContextRa = cra; ContextDec = cdec;
            return;
        }
        _down = at; _downProjection = Projection; _dragged = false;
        _drag = HitFrame(at);
        if (_drag == Drag.MoveFrame && Frame is { } f)
        {
            var (ra, dec) = _downProjection.Unproject(at.X, at.Y);
            Gnomonic.TryFromSkyPlane(f.RaHours, f.DecDegrees, ra, dec, out var gx, out var gy);
            _grab = (gx, gy);
        }
        if (_drag == Drag.None) _drag = Drag.Pan;
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var now = e.GetPosition(this);
        if (_down is not { } start)
        {
            // hovering: say what a press would do
            Cursor = HitFrame(now) switch
            {
                Drag.MoveFrame => new Cursor(StandardCursorType.SizeAll), Drag.ResizeFrame => new Cursor(StandardCursorType.BottomRightCorner),
                Drag.RotateFrame => new Cursor(StandardCursorType.Hand), _ => new Cursor(StandardCursorType.Arrow),
            };
            return;
        }
        double dx = now.X - start.X, dy = now.Y - start.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        if (_drag == Drag.Pan)
        {
            // keep the sky point that was under the pointer under it
            var (ra, dec) = _downProjection.Unproject(_downProjection.Width / 2 - dx, _downProjection.Height / 2 - dy);
            CenterRa = ra; CenterDec = Math.Clamp(dec, -90, 90);
        }
        else EditFrame(now);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_down is not null && !_dragged && _drag == Drag.Pan)
        {
            var p = e.GetPosition(this);
            var proj = Projection;
            var (ra, dec) = proj.Unproject(p.X, p.Y);
            var arg = (ra, dec, proj.PixelsPerDegree);
            if (PickCommand?.CanExecute(arg) == true) PickCommand.Execute(arg);
        }
        _down = null; _drag = Drag.None; e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // zoom towards the pointer: the sky point under it stays under it
        var at = e.GetPosition(this);
        var before = Projection;
        var (sra, sdec) = before.Unproject(at.X, at.Y);
        double fov = Math.Clamp(Fov * Math.Pow(0.85, e.Delta.Y), 0.1, 180);
        double cra = CenterRa, cdec = CenterDec;
        for (int k = 0; k < 4; k++)       // each shift of the centre changes the projection a little: settle on it
        {
            var after = new SkyProjection(cra, cdec, fov, before.Width, before.Height);
            if (!after.TryProject(sra, sdec, out var qx, out var qy)) break;
            (cra, cdec) = after.Unproject(before.Width / 2 + (qx - at.X), before.Height / 2 + (qy - at.Y));
            cdec = Math.Clamp(cdec, -90, 90);
        }
        CenterRa = cra; CenterDec = cdec;
        Fov = fov;
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
        DrawSkyGlow(ctx, proj);
        DrawImages(ctx, proj);
        if (ShowGrid) DrawGrid(ctx, proj);
        if (ShowConstellations) DrawConstellations(ctx, proj);
        DrawDsos(ctx, proj);
        DrawStars(ctx, proj);
        DrawBodies(ctx, proj);
        DrawHorizon(ctx, proj);
        DrawPolygons(ctx, proj);
        DrawFrame(ctx, proj);
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

    /// <summary>The colour an object glows with in a picture: hydrogen red for emission nebulae and remnants, blue for the
    /// reflection nebulae (the dust round bright blue stars), teal for planetaries, the warm white of old stars for galaxies and
    /// globulars. Clusters and dark nebulae have no glow of their own (their stars are drawn).</summary>
    public static DsoTextures.Look? LookOf(ChartDso d)
    {
        string name = d.CommonName.ToLowerInvariant();
        if (d.Kind is "Nebula" && (name.Contains("reflection") || name.Contains("merope") || name.Contains("maia") || name.Contains("witch head")
                || name.Contains("iris") || name.Contains("running man") || name.Contains("rho oph") || name.Contains("blue horsehead")))
            return DsoTextures.Look.Reflection;
        return d.Kind switch
        {
            "Galaxy" => DsoTextures.Look.Galaxy,
            "GlobularCluster" => DsoTextures.Look.Globular,
            "Nebula" => DsoTextures.Look.Emission,
            "SupernovaRemnant" => DsoTextures.Look.Remnant,
            "PlanetaryNebula" => DsoTextures.Look.Planetary,
            _ => null,
        };
    }

    /// <summary>How strongly an object shows (0..1), from its surface brightness: its magnitude spread over its area. Objects
    /// with no magnitude (most nebulae) get a middling strength.</summary>
    public static double GlowStrength(ChartDso d)
    {
        double major = Math.Max(d.MajorArcmin, 0.3), minor = d.MinorArcmin > 0 ? d.MinorArcmin : major;
        if (float.IsNaN(d.Magnitude)) return d.Kind is "Nebula" or "SupernovaRemnant" ? 0.55 : 0.4;
        double sb = d.Magnitude + 2.5 * Math.Log10(Math.PI / 4 * major * minor);   // magnitudes per square arcminute
        return Math.Clamp(1.15 - (sb - 9) * 0.08, 0.18, 1);
    }

    private void DrawDsos(DrawingContext ctx, SkyProjection proj)
    {
        if (Dsos is not { } dsos) return;
        double ppd = proj.PixelsPerDegree;
        // the pictures first, faintest first, so bright cores sit on top of the haze round them
        foreach (var d in dsos.OrderBy(GlowStrength))
        {
            if (LookOf(d) is not { } look || !proj.TryProject(d.RaHours, d.DecDegrees, out var x, out var y) || !Visible(x, y, 600)) continue;
            double a = d.MajorArcmin / 60 * ppd / 2, b = (d.MinorArcmin > 0 ? d.MinorArcmin : d.MajorArcmin) / 60 * ppd / 2;
            if (a < 1.2) continue;
            b = Math.Max(b, a * 0.06);
            double hw = b * DsoTextures.Reach, hh = a * DsoTextures.Reach;
            var tex = DsoTextures.Get(d.Id, look, b / a, 2 * hh);
            // a tiny object is a faint smudge, not a bright blob; a big one shows at its full strength
            double opacity = GlowStrength(d) * Math.Clamp(a / 6, 0.35, 1);
            using (ctx.PushTransform(Matrix.CreateRotation(-d.PositionAngle * Math.PI / 180) * Matrix.CreateTranslation(x, y)))
            using (ctx.PushOpacity(opacity))
                ctx.DrawImage(tex, new Rect(0, 0, tex.PixelSize.Width, tex.PixelSize.Height), new Rect(-hw, -hh, 2 * hw, 2 * hh));
        }
        foreach (var d in dsos)
        {
            if (!proj.TryProject(d.RaHours, d.DecDegrees, out var x, out var y) || !Visible(x, y, 200)) continue;
            double a = Math.Max(3, d.MajorArcmin / 60 * ppd / 2), b = Math.Max(3, (d.MinorArcmin > 0 ? d.MinorArcmin : d.MajorArcmin) / 60 * ppd / 2);
            var color = KindColor(d.Kind);
            // what has a picture needs no outline; clusters and dark nebulae get a light one, so they can be found
            bool pictured = LookOf(d) is not null && d.MajorArcmin / 60 * ppd / 2 >= 1.2;
            if (!pictured)
            {
                var pen = new Pen(new SolidColorBrush(color, 0.38), 0.9, d.Kind is "OpenCluster" or "Asterism" ? new DashStyle(new double[] { 3, 3 }, 0) : null);
                using (ctx.PushTransform(Matrix.CreateRotation(-d.PositionAngle * Math.PI / 180) * Matrix.CreateTranslation(x, y)))
                    ctx.DrawEllipse(null, pen, new Point(0, 0), b, a);
            }
            bool label = Fov < 25 ? (a > 6 || (!float.IsNaN(d.Magnitude) && d.Magnitude < 10)) : !float.IsNaN(d.Magnitude) && d.Magnitude < 6.5;
            if (label)
            {
                string text = d.CommonName != "" && Fov < 40 && d.CommonName.Length < 28 ? $"{d.Id}  {d.CommonName}" : d.Id;
                Text(ctx, text, x + Math.Min(b, 60) + 4, y - 6, new SolidColorBrush(color, 0.7), 10);
            }
        }
    }

    /// <summary>Star colour from its B-V index, as a star looks in a picture: B-V to temperature (Ballesteros), the temperature's
    /// black-body colour, then about half way back to white (stars are pale; only the hot and the cool ones are clearly tinted).</summary>
    public static Color StarColor(float bv)
    {
        double b = float.IsNaN(bv) ? 0.6 : Math.Clamp(bv, -0.4, 2.0);
        double t = 4600 * (1 / (0.92 * b + 1.7) + 1 / (0.92 * b + 0.62));
        var (r, g, bl) = BlackBody(t);
        double m = Math.Max(r, Math.Max(g, bl));
        r /= m; g /= m; bl /= m;
        const double keep = 0.55;
        return Color.FromRgb((byte)(255 * (1 - keep + keep * r)), (byte)(255 * (1 - keep + keep * g)), (byte)(255 * (1 - keep + keep * bl)));
    }

    /// <summary>The sRGB colour of a black body (Tanner Helland's fit), 0..1 a channel.</summary>
    private static (double R, double G, double B) BlackBody(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100, r, g, b;
        r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return (Math.Clamp(r, 0, 255) / 255, Math.Clamp(g, 0, 255) / 255, Math.Clamp(b, 0, 255) / 255);
    }

    /// <summary>Core radius in pixels for a magnitude: grows with the star's light (about a third root of the flux, as a star's
    /// image on a sensor does), from a pinpoint at the faintest drawn.</summary>
    public static double StarRadius(float magnitude, double limit)
    {
        double flux = Math.Pow(10, -0.4 * (magnitude - limit));
        return Math.Clamp(0.42 + 0.5 * Math.Pow(Math.Max(flux, 0), 0.3), 0.5, 7.5);
    }

    /// <summary>How opaque a star's core is: the faintest are barely there, a few magnitudes up they are solid.</summary>
    public static double StarAlpha(float magnitude, double limit) => Math.Clamp(0.22 + 0.26 * (limit - magnitude), 0.22, 1);

    private readonly Dictionary<(int, int), IBrush> _starBrushes = new();
    private readonly Dictionary<int, IBrush> _haloBrushes = new();
    private readonly Dictionary<int, IBrush> _spikeBrushes = new();

    private static Color Toward(Color c, Color to, double t) =>
        Color.FromArgb(c.A, (byte)(c.R + (to.R - c.R) * t), (byte)(c.G + (to.G - c.G) * t), (byte)(c.B + (to.B - c.B) * t));

    private void DrawStars(DrawingContext ctx, SkyProjection proj)
    {
        if (Stars is not { } stars) return;
        double limit = StarLimit;
        foreach (var s in stars.OrderByDescending(s => s.Magnitude))     // faint first: bright stars on top
        {
            if (!proj.TryProject(s.RaHours, s.DecDegrees, out var x, out var y) || !Visible(x, y, 60)) continue;
            int ckey = float.IsNaN(s.ColorIndex) ? 99 : (int)Math.Round((s.ColorIndex + 0.4) * 20);
            var tint = StarColor(s.ColorIndex);
            double r = StarRadius(s.Magnitude, limit);
            double above = limit - s.Magnitude;              // magnitudes above the faintest shown
            if (above > 2.2)
            {
                // the bloom: light scattered round a bright star, in its colour (stronger than the core's, as in a photograph)
                if (!_haloBrushes.TryGetValue(ckey, out var halo))
                {
                    var c = Toward(tint, Color.FromRgb(tint.R < 200 ? (byte)90 : (byte)255, tint.G, tint.B < 200 ? (byte)90 : (byte)255), 0.25);
                    _haloBrushes[ckey] = halo = new RadialGradientBrush { GradientStops = new GradientStops
                    {
                        new GradientStop(Color.FromArgb(190, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(80, c.R, c.G, c.B), 0.18),
                        new GradientStop(Color.FromArgb(26, c.R, c.G, c.B), 0.45), new GradientStop(Color.FromArgb(7, c.R, c.G, c.B), 0.75),
                        new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                    } };
                }
                double hr = r * (2.6 + 0.55 * Math.Min(above, 9));
                ctx.DrawEllipse(halo, null, new Point(x, y), hr, hr);
                if (above > 5.5)
                {
                    // the very brightest: faint diffraction spikes
                    if (!_spikeBrushes.TryGetValue(ckey, out var spike))
                        _spikeBrushes[ckey] = spike = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                            GradientStops = new GradientStops { new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0), new GradientStop(Color.FromArgb(120, tint.R, tint.G, tint.B), 0.5), new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1) },
                        };
                    double len = r * (5 + 1.2 * (above - 5.5));
                    ctx.FillRectangle(spike, new Rect(x - len, y - 0.6, 2 * len, 1.2));
                    using (ctx.PushTransform(Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(x, y)))
                        ctx.FillRectangle(spike, new Rect(-len, -0.6, 2 * len, 1.2));
                }
            }
            // the core: white-hot in the middle of bright stars (a saturated star is white), the true tint in faint ones
            int akey = (int)Math.Round(StarAlpha(s.Magnitude, limit) * 10);
            double white = Math.Clamp((above - 1.5) / 5, 0, 0.75);
            int wkey = (int)Math.Round(white * 8);
            if (!_starBrushes.TryGetValue((ckey * 16 + wkey, akey), out var brush))
                _starBrushes[(ckey * 16 + wkey, akey)] = brush = new SolidColorBrush(Toward(tint, Colors.White, wkey / 8.0), akey / 10.0);
            ctx.DrawEllipse(brush, null, new Point(x, y), r, r);
            if (s.Label != "" && (s.Magnitude < 2.2 || (Fov < 40 && s.Magnitude < 4.5) || Fov < 12)) Text(ctx, s.Label, x + r + 3, y - 6, StarText, 10);
        }
    }

    // ---- the Milky Way --------------------------------------------------------------------------------------------

    private Avalonia.Media.Imaging.WriteableBitmap? _glowBitmap;
    private (double, double, double, double, double, SkyGlowMap?) _glowKey;
    private const int GlowStep = 3;          // one glow pixel per 3x3 screen pixels: it is smooth anyway

    /// <summary>The Milky Way as a soft band: every few screen pixels looks up its sky position in the glow map. Rebuilt only
    /// when the view changes.</summary>
    private void DrawSkyGlow(DrawingContext ctx, SkyProjection proj)
    {
        if (SkyGlow is not { } map) return;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width / GlowStep)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height / GlowStep));
        var key = (CenterRa, CenterDec, Fov, Bounds.Width, Bounds.Height, map);
        if (_glowBitmap is null || _glowKey != key || _glowBitmap.PixelSize.Width != w || _glowBitmap.PixelSize.Height != h)
        {
            _glowBitmap?.Dispose();
            _glowBitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            var px = new int[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    var (ra, dec) = proj.Unproject((i + 0.5) * GlowStep, (j + 0.5) * GlowStep);
                    if (double.IsNaN(ra) || double.IsNaN(dec)) continue;
                    double v = map.Sample(ra, dec);
                    if (v <= 0.002) continue;
                    // a faint, slightly cool-white band; the dust lanes show as the gaps between the contours
                    double a = Math.Pow(v, 0.85) * 0.5;
                    int A = (int)(255 * a), R = (int)(205 * a), G = (int)(200 * a), B = (int)(215 * a);
                    px[j * w + i] = (A << 24) | (R << 16) | (G << 8) | B;
                }
            using (var fb = _glowBitmap.Lock())
                for (int j = 0; j < h; j++) System.Runtime.InteropServices.Marshal.Copy(px, j * w, fb.Address + j * fb.RowBytes, w);
            _glowKey = key;
        }
        ctx.DrawImage(_glowBitmap, new Rect(0, 0, w, h), new Rect(0, 0, w * GlowStep, h * GlowStep));
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

    /// <summary>Rasters laid on the sky, each cut into tiles that are placed one by one (see <see cref="ChartGeometry.Tiles"/>).</summary>
    private void DrawImages(DrawingContext ctx, SkyProjection proj)
    {
        if (Images is not { } images) return;
        foreach (var im in images)
        {
            double w = Math.Max(1, im.Bitmap.Size.Width), h = Math.Max(1, im.Bitmap.Size.Height);
            using (ctx.PushOpacity(im.Opacity))
                foreach (var (src, map) in ChartGeometry.Tiles(proj, im.PixelToSky, w, h))
                    using (ctx.PushTransform(map))
                        ctx.DrawImage(im.Bitmap, src, new Rect(src.X - 0.25, src.Y - 0.25, src.Width + 0.5, src.Height + 0.5));
        }
    }

    private static readonly Color FrameColor = Color.FromRgb(255, 213, 79);

    private void DrawFrame(DrawingContext ctx, SkyProjection proj)
    {
        if (Frame is not { } f || !FrameGeometry(proj, out var c, out var outline, out var centre, out var handle)) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(outline[0], true);
            for (int k = 1; k < outline.Count; k++) g.LineTo(outline[k]);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(FrameColor, 0.10), new Pen(new SolidColorBrush(FrameColor), 2), geo);
        // where the frame's up points: from the top edge to the handle
        double dx = handle.X - centre.X, dy = handle.Y - centre.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        ctx.DrawLine(new Pen(new SolidColorBrush(FrameColor), 1.5), new Point(handle.X - dx / len * 28, handle.Y - dy / len * 28), handle);
        if (f.Editable)
        {
            var fill = new SolidColorBrush(Color.FromRgb(15, 19, 26)); var edge = new Pen(new SolidColorBrush(FrameColor), 2);
            foreach (var p in c) ctx.DrawRectangle(fill, edge, new Rect(p.X - HandleRadius / 2 - 1, p.Y - HandleRadius / 2 - 1, HandleRadius + 2, HandleRadius + 2));
            ctx.DrawEllipse(new SolidColorBrush(FrameColor), null, handle, HandleRadius - 1, HandleRadius - 1);
        }
        string size = f.WidthDegrees >= 1 ? $"{f.WidthDegrees:0.##}° × {f.HeightDegrees:0.##}°" : $"{f.WidthDegrees * 60:0.#}′ × {f.HeightDegrees * 60:0.#}′";
        string text = (f.Label != "" ? f.Label + "   " : "") + size + (Math.Abs(f.AngleDegrees) > 0.05 ? $"   {f.AngleDegrees:0.#}°" : "");
        double minX = outline.Min(p => p.X), maxY = outline.Max(p => p.Y);
        Text(ctx, text, minX, maxY + 6, new SolidColorBrush(FrameColor), 12);
    }

    private void DrawPolygons(DrawingContext ctx, SkyProjection proj)
    {
        if (Polygons is not { } polys) return;
        foreach (var poly in polys)
        {
            var pen = new Pen(new SolidColorBrush(poly.Color), 1.5);
            var pts = ChartGeometry.Outline(proj, poly.Corners);
            if (pts is null || pts.Count < 2) continue;
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
