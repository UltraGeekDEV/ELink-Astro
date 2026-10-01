using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ELink.Core.Astro;
using ELink.UI.Controls;
using Xunit;

namespace ELink.UI.Tests;

public class SkyChartTests
{
    private static unsafe (byte R, byte G, byte B) Pixel(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        byte* p = (byte*)fb.Address + y * fb.RowBytes + x * 4;
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (p[0], p[1], p[2]) : (p[2], p[1], p[0]);
    }

    private static WriteableBitmap Snapshot(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame()!;
        var copy = new WriteableBitmap(frame.PixelSize, frame.Dpi, frame.Format ?? Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = copy.Lock()) frame.CopyPixels(new PixelRect(frame.PixelSize), fb.Address, fb.RowBytes * frame.PixelSize.Height, fb.RowBytes);
        return copy;
    }

    [AvaloniaFact]
    public void StarsAreDrawnWhereTheProjectionPutsThem()
    {
        var chart = new SkyChart
        {
            CenterRa = 5.6, CenterDec = 0, Fov = 40, StarLimit = 6, ShowGrid = false, ShowConstellations = false,
            Stars = new[] { new ChartStar(5.919f, 7.407f, 0.45f, 1.85f, ""), new ChartStar(5.242f, -8.2f, 0.13f, -0.03f, "") },
        };
        var w = new Window { Width = 800, Height = 600, Content = chart };
        w.Show();
        var bmp = Snapshot(w);
        var proj = new SkyProjection(5.6, 0, 40, chart.Bounds.Width, chart.Bounds.Height);
        Assert.True(proj.TryProject(5.919, 7.407, out var bx, out var by));          // Betelgeuse: red-orange, north-east of the centre
        var b = Pixel(bmp, (int)bx, (int)by);
        Assert.True(b.R > 200 && b.R > b.B, $"Betelgeuse pixel {b}");
        Assert.True(bx < 400 && by < 300, "east is left, north is up");
        Assert.True(proj.TryProject(5.242, -8.2, out var rx, out var ry));          // Rigel: blue-white, south-west
        var r = Pixel(bmp, (int)rx, (int)ry);
        Assert.True(r.B > 200 && r.B >= r.R, $"Rigel pixel {r}");
        var empty = Pixel(bmp, 400, 300);                                             // nothing at the centre: background
        Assert.True(empty.R < 30 && empty.G < 30 && empty.B < 40, $"background {empty}");
        w.Close();
    }

    [AvaloniaFact]
    public void ZoomingWithTheWheelAndPickingWithAClick()
    {
        (double Ra, double Dec, double Ppd)? picked = null;
        var chart = new SkyChart { CenterRa = 12, CenterDec = 20, Fov = 30, PickCommand = new Relay(p => picked = ((double, double, double))p!) };
        var w = new Window { Width = 800, Height = 600, Content = chart };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.MouseWheel(new Point(400, 300), new Vector(0, 2));
        Assert.True(chart.Fov < 30, $"fov {chart.Fov}");
        w.MouseDown(new Point(400, 300), Avalonia.Input.MouseButton.Left);
        w.MouseUp(new Point(400, 300), Avalonia.Input.MouseButton.Left);
        Assert.NotNull(picked);
        Assert.Equal(12, picked!.Value.Ra, 3); Assert.Equal(20, picked.Value.Dec, 3);
        // a drag pans instead of picking
        picked = null;
        w.MouseDown(new Point(400, 300), Avalonia.Input.MouseButton.Left);
        w.MouseMove(new Point(500, 300));
        w.MouseUp(new Point(500, 300), Avalonia.Input.MouseButton.Left);
        Assert.Null(picked);
        Assert.True(chart.CenterRa > 12, "dragging the sky to the right brings eastern (higher RA) sky into the middle");
        w.Close();
    }

    private sealed class Relay(Action<object?> run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => run(p);
    }
}
