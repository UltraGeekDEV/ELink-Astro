using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ELink.UI.Controls;

/// <summary>Shows a picture that can be zoomed (wheel, towards the pointer), panned (drag) and fitted (double-click). A new picture fades in over
/// the old one, and <see cref="Flash"/> shows where a new frame landed: a white shape on the picture that fades away.</summary>
public sealed class PictureViewer : Control
{
    public static readonly StyledProperty<Bitmap?> ImageProperty = AvaloniaProperty.Register<PictureViewer, Bitmap?>(nameof(Image));
    public Bitmap? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    private Bitmap? _old, _current;
    private double _fade = 1;                       // 0..1: how far the new picture has faded in
    private double _scale = 1; private Point _offset;
    private bool _fitted;
    private Point? _drag; private Point _dragFrom, _dragOffset;
    private readonly List<(Point[] Quad, DateTime At)> _flashes = new();
    private DispatcherTimer? _timer;
    private DateTime _fadeStart;
    private static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(1500), FadeTime = TimeSpan.FromMilliseconds(650);

    static PictureViewer()
    {
        ImageProperty.Changed.AddClassHandler<PictureViewer>((v, e) => v.OnImage(e.NewValue as Bitmap));
        AffectsRender<PictureViewer>(ImageProperty);
        FocusableProperty.OverrideDefaultValue<PictureViewer>(true);
    }

    private void OnImage(Bitmap? image)
    {
        bool sameSize = _current is not null && image is not null && _current.PixelSize == image.PixelSize;
        _old = sameSize ? _current : null;                 // a refreshed picture of the same field fades in; a different one just replaces
        _current = image;
        _fade = sameSize ? 0 : 1; _fadeStart = DateTime.UtcNow;
        if (!sameSize) _fitted = false;
        Tick(true);
        InvalidateVisual();
    }

    /// <summary>Shows where a frame landed: the corners as fractions of the picture's width and height, as it is shown.</summary>
    public void Flash(IReadOnlyList<Point> quad)
    {
        if (quad.Count != 4) return;
        _flashes.Add((quad.ToArray(), DateTime.UtcNow));
        if (_flashes.Count > 40) _flashes.RemoveAt(0);
        Tick(true);
    }

    /// <summary>Runs the animation clock while something is moving.</summary>
    private void Tick(bool start)
    {
        if (start && _timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Tick(false);
            _timer.Start();
        }
        var now = DateTime.UtcNow;
        _flashes.RemoveAll(f => now - f.At > FlashTime);
        if (_fade < 1) { _fade = Math.Min(1, (now - _fadeStart) / FadeTime); if (_fade >= 1) _old = null; }
        if (_flashes.Count == 0 && _fade >= 1) { _timer?.Stop(); _timer = null; }
        InvalidateVisual();
    }

    private void Fit()
    {
        if (_current is null || Bounds.Width < 10 || Bounds.Height < 10) return;
        double s = Math.Min(Bounds.Width / _current.Size.Width, Bounds.Height / _current.Size.Height) * 0.98;
        _scale = s; _offset = new Point((Bounds.Width - _current.Size.Width * s) / 2, (Bounds.Height - _current.Size.Height * s) / 2);
        _fitted = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); _fitted = false; InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x0B, 0x10)), null, new Rect(Bounds.Size));
        if (_current is null) return;
        if (!_fitted) Fit();
        var dest = new Rect(_offset.X, _offset.Y, _current.Size.Width * _scale, _current.Size.Height * _scale);
        using (ctx.PushClip(new Rect(Bounds.Size)))
        {
            var quality = _scale < 1 ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = quality }))
            {
                if (_old is not null) ctx.DrawImage(_old, new Rect(_old.Size), dest);
                using (ctx.PushOpacity(_fade)) ctx.DrawImage(_current, new Rect(_current.Size), dest);
            }
            // a soft edge round the picture
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 140, 170, 220)), 1), dest);
            // frames that have just landed
            var now = DateTime.UtcNow;
            foreach (var (quad, at) in _flashes)
            {
                double t = Math.Clamp((now - at) / FlashTime, 0, 1), a = (1 - t) * (1 - t);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(P(quad[0], dest), true);
                    for (int i = 1; i < 4; i++) c.LineTo(P(quad[i], dest));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(150 * a), 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * a), 255, 255, 255)), 2), g);
            }
        }
    }

    private static Point P(Point n, Rect dest) => new(dest.X + n.X * dest.Width, dest.Y + n.Y * dest.Height);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_current is null) return;
        if (!_fitted) Fit();
        var p = e.GetPosition(this);
        double factor = Math.Pow(1.0015, e.Delta.Y * 120), min = 0.02, max = 40;
        double ns = Math.Clamp(_scale * factor, min, max);
        factor = ns / _scale;
        _offset = new Point(p.X - (p.X - _offset.X) * factor, p.Y - (p.Y - _offset.Y) * factor);
        _scale = ns;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { _fitted = false; InvalidateVisual(); return; }
        _drag = e.GetPosition(this); _dragFrom = _drag.Value; _dragOffset = _offset;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;
        var p = e.GetPosition(this);
        _offset = new Point(_dragOffset.X + p.X - _dragFrom.X, _dragOffset.Y + p.Y - _dragFrom.Y);
        _fitted = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _drag = null; e.Pointer.Capture(null); }
}
