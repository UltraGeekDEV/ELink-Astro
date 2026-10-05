using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ELink.UI.Controls;

public sealed record FlatPoint(double Azimuth, double Altitude, bool IsHour = false);
/// <summary>A path across the sky (a target's tonight, hour by hour) with where it is now; with no points only the marker is drawn.</summary>
public sealed record FlatTrack(string Label, Color Color, IReadOnlyList<FlatPoint> Points, FlatPoint Now);
public sealed record FlatBody(string Label, double Azimuth, double Altitude, Color Color, double Radius);
/// <summary>Everything the flat horizon draws: the horizon (altitude for each whole azimuth 0..360), what is in the sky, the tracks.</summary>
public sealed record FlatHorizonData(double CentreAzimuth, IReadOnlyList<double> Profile, IReadOnlyList<FlatBody> Bodies, IReadOnlyList<FlatTrack> Tracks);

/// <summary>The sky laid out flat: azimuth along the bottom (the middle of the view is south in the north, north in the south, so
/// the part of the sky you use is in the middle), altitude up the side, the horizon with what blocks it, the Sun, Moon and planets, and
/// the paths targets take (hours marked, now bright).</summary>
public sealed class HorizonPlot : Control
{
    public static readonly StyledProperty<FlatHorizonData?> DataProperty = AvaloniaProperty.Register<HorizonPlot, FlatHorizonData?>(nameof(Data));
    public FlatHorizonData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    static HorizonPlot() { AffectsRender<HorizonPlot>(DataProperty); AffectsMeasure<HorizonPlot>(DataProperty); }

    public const double MinAlt = -6, MaxAlt = 90;
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 190 : Math.Min(availableSize.Height, 190));

    private static readonly Typeface Face = new(FontFamily.Default);

    /// <summary>Where an azimuth falls along the plot (0..1), given the azimuth in the middle.</summary>
    public static double XOf(double azimuth, double centre) => ((azimuth - centre + 540) % 360 + 360) % 360 / 360;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 60) return;
        double left = 30, right = 8, top = 6, bottom = 18, pw = w - left - right, ph = h - top - bottom;
        var d = Data;
        double centre = d?.CentreAzimuth ?? 180;
        double X(double az) => left + XOf(az, centre) * pw;
        double Y(double alt) => top + (MaxAlt - Math.Clamp(alt, MinAlt, MaxAlt)) / (MaxAlt - MinAlt) * ph;
        void Text(string s, double x, double y, IBrush b, double size = 10.5, bool middle = true, bool bold = false)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold) : Face, size, b);
            ctx.DrawText(ft, new Point(middle ? x - ft.Width / 2 : x, y));
        }
        var faint = new SolidColorBrush(Color.FromRgb(0x8A, 0x98, 0xB3));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x48)), 1);

        // the sky, deeper blue overhead
        ctx.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0x0C, 0x11, 0x1D), 0), new GradientStop(Color.FromRgb(0x1D, 0x2B, 0x44), 1) },
        }, null, new Rect(left, top, pw, ph), 4, 4);
        for (int alt = 0; alt <= 75; alt += 15)
        {
            ctx.DrawLine(grid, new Point(left, Y(alt)), new Point(left + pw, Y(alt)));
            Text($"{alt}°", left - 4, Y(alt) - 7, faint, 9.5, middle: false);
        }
        for (int az = 0; az < 360; az += 30)
        {
            double x = X(az);
            if (x <= left + 1 || x >= left + pw - 1) continue;
            ctx.DrawLine(grid, new Point(x, top), new Point(x, top + ph));
        }

        // the horizon, and what stands above it
        if (d is { Profile.Count: > 0 })
        {
            var fill = new StreamGeometry();
            using (var g = fill.Open())
            {
                bool started = false; double lastX = left;
                for (int i = 0; i <= 360; i++)
                {
                    double az = (centre - 180 + i) % 360, x = left + i / 360.0 * pw;
                    double alt = d.Profile[(int)Math.Round(az) % d.Profile.Count];
                    if (!started) { g.BeginFigure(new Point(x, top + ph), true); started = true; }
                    g.LineTo(new Point(x, Y(Math.Max(alt, 0)))); lastX = x;
                }
                g.LineTo(new Point(lastX, top + ph)); g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x33)), new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x99)), 1.4), fill);
        }
        else
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x33)), null, new Rect(left, Y(0), pw, top + ph - Y(0)));

        // cardinal directions and degrees
        foreach (var (name, az) in new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) })
            Text(name, X(az), top + ph + 2, new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF5)), 11, bold: true);
        for (int az = 0; az < 360; az += 30)
            if (az % 90 != 0) Text($"{az}°", X(az), top + ph + 3, faint, 9.5);

        if (d is null) return;
        // the paths
        foreach (var t in d.Tracks)
        {
            var brush = new SolidColorBrush(t.Color);
            var pen = new Pen(brush, 1.8);
            FlatPoint? prev = null;
            foreach (var p in t.Points)
            {
                if (prev is { } q && p.Altitude > MinAlt && q.Altitude > MinAlt && Math.Abs(X(p.Azimuth) - X(q.Azimuth)) < pw / 2)
                    ctx.DrawLine(pen, new Point(X(q.Azimuth), Y(q.Altitude)), new Point(X(p.Azimuth), Y(p.Altitude)));
                if (p.IsHour && p.Altitude > MinAlt) ctx.DrawEllipse(brush, null, new Point(X(p.Azimuth), Y(p.Altitude)), 2, 2);
                prev = p;
            }
            if (t.Now.Altitude > MinAlt)
            {
                var c = new Point(X(t.Now.Azimuth), Y(t.Now.Altitude));
                ctx.DrawEllipse(brush, new Pen(Brushes.White, 1.5), c, 5, 5);
                Text(t.Label + (t.Now.Altitude < 0 ? " (below)" : $" {t.Now.Altitude:0}°"), c.X + 8, c.Y - 7, brush, 10.5, middle: false);
            }
        }
        // the Sun, Moon and planets
        foreach (var b in d.Bodies)
        {
            if (b.Altitude < MinAlt) continue;
            var c = new Point(X(b.Azimuth), Y(b.Altitude));
            double r = b.Radius > 0 ? 6 : 3.5;
            var color = b.Altitude < 0 ? Color.FromArgb(110, b.Color.R, b.Color.G, b.Color.B) : b.Color;
            ctx.DrawEllipse(new SolidColorBrush(color), null, c, r, r);
            Text(b.Label, c.X + r + 3, c.Y - 7, new SolidColorBrush(color), 10, middle: false);
        }
    }
}
