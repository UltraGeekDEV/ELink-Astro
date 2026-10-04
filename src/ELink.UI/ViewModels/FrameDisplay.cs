using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using ELink.Imaging;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>Shows frames: decodes FITS off the UI thread, stretches for display, and hands a bitmap to the view.</summary>
public sealed partial class FrameDisplay : ObservableObject
{
    [ObservableProperty] private WriteableBitmap? _image;
    /// <summary>Where the shown frame lies on the sky, when its FITS header says (the stacked image does); null otherwise.</summary>
    [ObservableProperty] private TanWcs? _wcs;
    /// <summary>The shown frame's size in pixels, and whether its first row is the top one (see <see cref="Wcs"/>).</summary>
    public int PixelsWide { get; private set; }
    public int PixelsHigh { get; private set; }
    public bool TopDown { get; private set; }
    [ObservableProperty] private string _info = "no frame yet";
    private int _generation;

    public void Show(byte[] data, string format, string caption)
    {
        int generation = Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            try
            {
                if (!format.Equals(".fits", StringComparison.OrdinalIgnoreCase) && !format.Equals(".fit", StringComparison.OrdinalIgnoreCase))
                { UiThread.Post(() => Info = $"{caption} ({format}, {data.Length / 1024} KiB: not displayable here)"); return; }
                var parsed = FitsImage.Parse(data);
                var fits = Debayer.ForDisplay(parsed);   // raw colour frames shown in colour
                var wcs = TanWcs.FromHeader(parsed.Header);
                var bgra = AutoStretch.ToBgra(fits);
                UiThread.Post(() =>
                {
                    if (generation != Volatile.Read(ref _generation)) return;   // a newer frame overtook this one
                    var bmp = new WriteableBitmap(new PixelSize(fits.Width, fits.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
                    PixelsWide = fits.Width; PixelsHigh = fits.Height; TopDown = parsed.TopDown;
                    Wcs = wcs;
                    Image = bmp;
                    Info = $"{caption}  {fits.Width}×{fits.Height}";
                });
            }
            catch (Exception ex) { UiThread.Post(() => Info = "cannot decode frame: " + ex.Message); }
        });
    }
}
