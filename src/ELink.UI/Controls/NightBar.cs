using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ELink.UI.Controls;

/// <summary>The night at a glance: a bar for the next 24 hours with the dark hours deep, the Moon's time above the horizon under it,
/// hour marks, and where now is.</summary>
public sealed class NightBar : Control
{
    public static readonly StyledProperty<NightTimeline?> TimelineProperty = AvaloniaProperty.Register<NightBar, NightTimeline?>(nameof(Timeline));
    public NightTimeline? Timeline { get => GetValue(TimelineProperty); set => SetValue(TimelineProperty, value); }
    static NightBar() { AffectsRender<NightBar>(TimelineProperty); AffectsMeasure<NightBar>(TimelineProperty); }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 74);

    private static readonly Typeface Face = new(FontFamily.Default);

    public override void Render(DrawingContext ctx)
    {
        if (Timeline is not { } t || t.To <= t.From) return;
        double w = Bounds.Width, barTop = 4, barH = 26;
        double X(DateTime d) => Math.Clamp((d - t.From).TotalHours / (t.To - t.From).TotalHours, 0, 1) * w;
        void Text(string s, double x, double y, IBrush b, double size = 11, bool centre = true)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b);
            ctx.DrawText(ft, new Point(centre ? x - ft.Width / 2 : x, y));
        }
        // day and twilight, then the dark hours over it
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x52, 0x78)), null, new Rect(0, barTop, w, barH), 6, 6);
        foreach (var (a, b) in t.Dark)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1C)), new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x48)), 1), new Rect(X(a), barTop, Math.Max(2, X(b) - X(a)), barH), 4, 4);
        // the Moon above the horizon
        foreach (var (a, b) in t.MoonUp)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xC0), 0.85), null, new Rect(X(a), barTop + barH + 5, Math.Max(2, X(b) - X(a)), 6), 3, 3);
        // hours
        var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x98, 0xB3)), 1);
        var label = new SolidColorBrush(Color.FromRgb(0xA9, 0xB5, 0xCB));
        var first = new DateTime(t.From.Year, t.From.Month, t.From.Day, t.From.Hour, 0, 0).AddHours(1);
        for (var h = first; h < t.To; h = h.AddHours(1))
        {
            if (h.Hour % 3 != 0) continue;
            double x = X(h);
            ctx.DrawLine(tick, new Point(x, barTop + barH + 14), new Point(x, barTop + barH + 18));
            Text(h.ToString("HH:mm", CultureInfo.InvariantCulture), x, barTop + barH + 19, label);
        }
        // now
        double nx = X(t.Now);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)), 2), new Point(nx, 0), new Point(nx, barTop + barH + 12));
        Text("now", Math.Clamp(nx, 14, w - 14), barTop + barH + 33, new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)));
    }
}

/// <summary>The next 24 hours at the site: when it is dark (astronomical night) and when the Moon is up, in local time.</summary>
public sealed record NightTimeline(DateTime Now, DateTime From, DateTime To, IReadOnlyList<(DateTime Start, DateTime End)> Dark, IReadOnlyList<(DateTime Start, DateTime End)> MoonUp);
