using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Core.Astro;
using ELink.Imaging;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>A framing assistant: take a picture (a photo of the sky from any camera or telescope, a frame from a file), find out where it is on the sky,
/// how big it is and how it is turned (a plate solve), show it on the chart where it belongs, and make the image to take exactly one frame
/// of it: same centre, same size, same turn.</summary>
public sealed partial class PictureFramingViewModel : ObservableObject
{
    private readonly MeshSession _mesh;
    private readonly ImageViewModel _image;

    public PictureFramingViewModel(MeshSession mesh, ImageViewModel image) { _mesh = mesh; _image = image; }

    /// <summary>Asks the person for a file (set by the window); returns its path or null.</summary>
    public Func<Task<string?>>? PickFile { get; set; }
    /// <summary>The chart should be drawn again (the picture came, went or was switched).</summary>
    public event Action? Changed;
    /// <summary>The image has just been framed like the picture: the chart should look at it.</summary>
    public event Action? Framed;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _messageKind = "info";
    [ObservableProperty] private bool _isSolving;
    [ObservableProperty] private bool _hasPicture;
    [ObservableProperty] private bool _showOnChart = true;
    [ObservableProperty] private string _fileName = "";
    partial void OnShowOnChartChanged(bool value) => Changed?.Invoke();

    public Bitmap? Bitmap { get; private set; }
    /// <summary>Where each pixel of <see cref="Bitmap"/> (x right, y down) lies on the sky: (RA hours, Dec degrees).</summary>
    public Func<double, double, (double RaHours, double DecDegrees)>? PixelToSky { get; private set; }

    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (PickFile is null) { Say("choosing a file is not possible here", "warn"); return; }
        var path = await PickFile();
        if (path is not null) await FrameFromAsync(path);
    }

    /// <summary>Solves the picture and frames the image like it. Says what it did, or why it could not.</summary>
    public async Task FrameFromAsync(string path)
    {
        IsSolving = true; Say($"looking at {Path.GetFileName(path)}…", "info");
        try
        {
            var loaded = await Task.Run(() => Load(path));
            if (loaded.Error is not null) { Say(loaded.Error, "error"); return; }
            var request = new SolveRequest { Image = new ELink.Contracts.RawBytes { Data = loaded.Fits! }, TimeoutSeconds = 150 };
            if (!double.IsNaN(loaded.RaHours)) { request.HintRaHours = loaded.RaHours; request.HintDecDegrees = loaded.DecDegrees; request.HintRadiusDegrees = 15; }
            if (loaded.ScaleArcsec > 0) { request.ScaleLowArcsecPerPixel = loaded.ScaleArcsec * 0.7; request.ScaleHighArcsecPerPixel = loaded.ScaleArcsec * 1.4; }
            Say("solving it (finding where on the sky it is)…", "info");
            var answers = await _mesh.Node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, request, TimeSpan.FromSeconds(request.TimeoutSeconds.Value + 40));
            var s = answers?.FirstOrDefault();
            if (s is null) { Say($"there is no plate solver on the mesh (install ASTAP or astrometry.net)", "warn"); return; }
            if (!s.Solved.Value) { Say("it could not be solved: " + (s.Message.Text == "" ? "no star pattern matched" : s.Message.Text), "error"); return; }

            var wcs = s.HasWcs.Value
                ? new TanWcs(s.WcsCrVal1.Value, s.WcsCrVal2.Value, s.WcsCrPix1.Value - 1, s.WcsCrPix2.Value - 1, s.WcsCd11.Value, s.WcsCd12.Value, s.WcsCd21.Value, s.WcsCd22.Value)
                : TanWcs.Centered(s.RaHours.Value * 15, s.DecDegrees.Value, double.IsNaN(s.PositionAngle.Value) ? 0 : s.PositionAngle.Value, s.PixelScale.Value, loaded.Width, loaded.Height);
            double scale = wcs.PixelScaleArcsec > 0 ? wcs.PixelScaleArcsec : s.PixelScale.Value;
            double widthDeg = loaded.Width * scale / 3600, heightDeg = loaded.Height * scale / 3600;

            // the picture on the chart
            Bitmap = loaded.Display; double h = loaded.Height; bool topDown = loaded.TopDown;
            double kx = loaded.Width / Math.Max(1.0, loaded.Display!.Size.Width), ky = loaded.Height / Math.Max(1.0, loaded.Display.Size.Height);   // the bitmap's pixels to the frame's
            PixelToSky = (px, py) => { double fx = px * kx, fy = py * ky; var (r, d) = wcs.PixelToSky(fx - 0.5, topDown ? fy - 0.5 : h - 0.5 - fy); return (r / 15, d); };
            double dispW = loaded.Display.Size.Width, dispH = loaded.Display.Size.Height;
            FileName = Path.GetFileName(path); HasPicture = true; ShowOnChart = true;
            // and the image to take: exactly one frame of it. Its centre and its turn come from how the picture lies on the sky as it is shown
            // (the solver solved the rows as they are stored, which for a top-down file is upside down), not from the solver's own angle
            var (cra, cdec) = PixelToSky(dispW / 2.0, dispH / 2.0);
            var (ura, udec) = PixelToSky(dispW / 2.0, dispH / 2.0 - dispH * 0.1);
            double dRa = (ura - cra) * 15 * Math.Cos(cdec * Math.PI / 180), dDec = udec - cdec;
            double angle = Math.Atan2(dRa, dDec) * 180 / Math.PI;
            _image.CenterRa = Sexagesimal.Format(cra, 0); _image.CenterDec = Sexagesimal.Format(cdec, 0);
            _image.Width = Math.Round(widthDeg, 4); _image.Height = Math.Round(heightDeg, 4); _image.PositionAngle = Math.Round(angle, 2);
            string size = widthDeg >= 1 ? $"{widthDeg:0.##}° × {heightDeg:0.##}°" : $"{widthDeg * 60:0.#}′ × {heightDeg * 60:0.#}′";
            Say($"Framed like {FileName}: {size}, turned {angle:0.#}°, {scale:0.##}″/px. The image to take is now exactly one frame of it.", "ok");
            Changed?.Invoke(); Framed?.Invoke();
        }
        catch (Exception ex) { Say("could not use that picture: " + ex.Message, "error"); }
        finally { IsSolving = false; }
    }

    [RelayCommand]
    private void Clear() { Bitmap = null; PixelToSky = null; HasPicture = false; FileName = ""; Say("", "info"); Changed?.Invoke(); }

    private void Say(string text, string kind) { MessageKind = kind; Message = text; }

    private sealed record Loaded(byte[]? Fits, Bitmap? Display, int Width, int Height, bool TopDown, double RaHours, double DecDegrees, double ScaleArcsec, string? Error);

    /// <summary>Reads the file: FITS as it is (and displayed with an automatic stretch), a picture (PNG, JPEG, TIFF) as the luminance of its pixels written as a FITS
    /// (the way a plate solver wants it) with its rows turned so that up is up.</summary>
    private static Loaded Load(string path)
    {
        if (!File.Exists(path)) return new Loaded(null, null, 0, 0, false, double.NaN, double.NaN, 0, "that file is not there");
        byte[] bytes = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".fits" or ".fit" or ".fts")
        {
            var img = FitsImage.Parse(bytes);
            var display = AutoStretch.ToBgra(Debayer.ForDisplay(img));
            var shown = Debayer.ForDisplay(img);
            var bmp = new WriteableBitmap(new PixelSize(shown.Width, shown.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock()) Marshal.Copy(display, 0, fb.Address, display.Length);
            double ra = img.GetDouble("RA", double.NaN) / 15, dec = img.GetDouble("DEC", double.NaN);
            if (double.IsNaN(ra) && Sexagesimal.TryParse(img.Get("OBJCTRA")?.Replace(' ', ':'), out var r2) && Sexagesimal.TryParse(img.Get("OBJCTDEC")?.Replace(' ', ':'), out var d2)) { ra = r2; dec = d2; }
            // (the solver and the WCS count the frame's own pixels, which a colour frame shown as 2x2 cells has twice as many of as the bitmap)
            return new Loaded(bytes, bmp, img.Width, img.Height, img.TopDown, ra, dec, img.GetDouble("SCALE", 0), null);
        }
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp")) return new Loaded(null, null, 0, 0, false, double.NaN, double.NaN, 0, "use a FITS file or a PNG, JPEG or TIFF picture");
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height, stride = w * 4;
        var buffer = new byte[stride * h];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, stride); }
        finally { handle.Free(); }
        var luma = new float[w * h];
        for (int y = 0; y < h; y++)                                  // FITS rows run from the bottom: the picture's last row is the first
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                luma[(h - 1 - y) * w + x] = (buffer[o] + buffer[o + 1] + buffer[o + 2]) / 3f * 257f;
            }
        var fits = FitsImage.WriteFloat32(w, h, luma, [("OBJECT", "'picture'")]);
        return new Loaded(fits, bitmap, w, h, false, double.NaN, double.NaN, 0, null);
    }
}
