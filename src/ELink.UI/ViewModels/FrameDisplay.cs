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
                var fits = Debayer.ForDisplay(FitsImage.Parse(data));   // raw colour frames shown in colour
                var bgra = AutoStretch.ToBgra(fits);
                UiThread.Post(() =>
                {
                    if (generation != Volatile.Read(ref _generation)) return;   // a newer frame overtook this one
                    var bmp = new WriteableBitmap(new PixelSize(fits.Width, fits.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, bgra.Length);
                    Image = bmp;
                    Info = $"{caption}  {fits.Width}×{fits.Height}";
                });
            }
            catch (Exception ex) { UiThread.Post(() => Info = "cannot decode frame: " + ex.Message); }
        });
    }
}
