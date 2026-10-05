using System.Globalization;
using ELink.Contracts.Automation;
using ELink.Core;
using ELink.Imaging;
using ELink.Imaging.Processing;
using Event.CoreFunctionality;

namespace ELink.Automation;

/// <summary>Makes pictures of the live stack: gradient removal, a neutral sky, an automatic stretch, colour touches. The stack itself is never
/// changed; <see cref="ProcessingIds.Export"/> saves the linear, unstretched stack (always) beside the picture.</summary>
public sealed class ProcessingService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly string _picturesDir;
    private readonly SemaphoreSlim _busy = new(2, 2);

    /// <param name="picturesDir">where exports go when the request does not say</param>
    public ProcessingService(TypeSafeEVentNode node, string picturesDir)
    {
        _node = node; _picturesDir = picturesDir;
        _commands = new CommandSet(node);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, ProcessAsync, "a processed picture of the live stack (gradient removed, stretched)");
        await _commands.AddAsync<ExportRequest, ExportResult>(ProcessingIds.Export, ExportAsync, "save the linear stack and the processed picture");
    }

    public static ProcessingParams Map(ProcessingSettings s) => new()
    {
        Stretch = s.Stretch.Value, BackgroundLevel = Math.Clamp(s.BackgroundLevel.Value, 0.02, 0.9), BlackClipSigmas = Math.Clamp(s.BlackClipSigmas.Value, 0, 8), Linked = s.Linked.Value,
        RemoveGradient = s.RemoveGradient.Value, GradientDegree = Math.Clamp(s.GradientDegree.Value, 1, 6), GradientDivide = s.GradientDivide.Value,
        NeutralizeBackground = s.NeutralizeBackground.Value, Saturation = Math.Clamp(s.Saturation.Value, 0, 3), GreenReduction = Math.Clamp(s.GreenReduction.Value, 0, 1),
    };

    private async Task<LiveStackImage?> FetchAsync(string source, double weight, string pseudo, int maxW, int maxH)
    {
        var answers = await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest
        {
            MaxWidth = maxW, MaxHeight = maxH, Neutralize = false, Filter = source, OutOfFocusWeight = weight, PseudoOutput = pseudo, KeepEmpty = true,
        }, TimeSpan.FromSeconds(60));
        return answers?.FirstOrDefault();
    }

    private async Task<ProcessedImage> ProcessAsync(ProcessRequest r)
    {
        await _busy.WaitAsync();
        try
        {
            var stack = await FetchAsync(r.Source.Text, r.OutOfFocusWeight.Value, r.PseudoOutput.Text, r.MaxWidth.Value, r.MaxHeight.Value);
            if (stack is null) return new ProcessedImage { Message = "no live stack on the mesh" };
            if (!stack.Ok.Value) return new ProcessedImage { Message = stack.Message.Text };
            return await Task.Run(() =>
            {
                var fits = FitsImage.Parse(stack.Image.Data);
                var (data, w, h, crop) = CropToData(fits);
                var pic = PictureProcessor.Process(data, w, h, fits.Channels, Map(r.Settings));
                bool flip = !fits.TopDown;
                var hist = new byte[pic.Histogram.Length];
                for (int i = 0; i < hist.Length; i++) hist[i] = (byte)Math.Round(pic.Histogram[i] * 255);
                return new ProcessedImage
                {
                    Ok = true, Width = w, Height = h, Channels = fits.Channels, CropLeft = crop.Left, CropTop = crop.Top, CropWidth = crop.Width, CropHeight = crop.Height, Frames = stack.Frames.Value, PixelScaleArcsec = stack.PixelScaleArcsec.Value,
                    ExposureSeconds = fits.GetDouble("EXPTIME", 0), FlipY = flip, GradientPercent = pic.GradientPercent, Note = pic.Note, Source = stack.Filter.Text,
                    Png = new ELink.Contracts.RawBytes { Data = PngWriter.Encode(pic.Display, w, h, fits.Channels, false, flip) },
                    Histogram = new ELink.Contracts.RawBytes { Data = hist },
                };
            });
        }
        catch (Exception ex) { return new ProcessedImage { Message = "processing failed: " + ex.Message }; }
        finally { _busy.Release(); }
    }

    private async Task<ExportResult> ExportAsync(ExportRequest r)
    {
        await _busy.WaitAsync();
        try
        {
            var stack = await FetchAsync(r.Source.Text, r.OutOfFocusWeight.Value, r.PseudoOutput.Text, 0, 0);
            if (stack is null) return new ExportResult { Message = "no live stack on the mesh" };
            if (!stack.Ok.Value) return new ExportResult { Message = stack.Message.Text };
            string dir = r.Directory.Text.Trim() != "" ? r.Directory.Text.Trim() : _picturesDir;
            Directory.CreateDirectory(dir);
            var fits = FitsImage.Parse(stack.Image.Data);
            string label = r.Label.Text.Trim() != "" ? r.Label.Text.Trim() : fits.Get("OBJECT")?.Trim('\'', ' ') ?? "stack";
            string stem = Sanitize(label) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var result = new ExportResult();
            // the linear data first: it is the part that cannot be made again
            string linear = Path.Combine(dir, stem + "_linear.fits");
            await File.WriteAllBytesAsync(linear + ".part", stack.Image.Data); File.Move(linear + ".part", linear, true);
            result.Files.Add(linear);
            if (r.PictureToo.Value)
            {
                var settings = Map(r.Settings);
                string png = Path.Combine(dir, stem + "_picture.png");
                await Task.Run(() =>
                {
                    var (data, w, h, _) = CropToData(fits);
                    var pic = PictureProcessor.Process(data, w, h, fits.Channels, settings);
                    File.WriteAllBytes(png + ".part", PngWriter.Encode(pic.Display, w, h, fits.Channels, sixteenBit: true, flipY: !fits.TopDown));
                    File.Move(png + ".part", png, true);
                    File.WriteAllText(Path.Combine(dir, stem + "_picture.txt"), System.Text.Json.JsonSerializer.Serialize(new { settings, pic.Note, frames = stack.Frames.Value, exposureSeconds = fits.GetDouble("EXPTIME", 0) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                });
                result.Files.Add(png);
            }
            result.Ok = true; result.Message = $"saved {result.Files.Count} file{(result.Files.Count == 1 ? "" : "s")} in {dir}";
            return result;
        }
        catch (Exception ex) { return new ExportResult { Message = "could not save: " + ex.Message }; }
        finally { _busy.Release(); }
    }

    /// <summary>The part of the stack that has data (the stack is a grid on the sky, and a tilted or dithered frame fills only part of it): the
    /// data cut to the box round it, and that box as fractions of the grid.</summary>
    private static (float[] Data, int Width, int Height, (double Left, double Top, double Width, double Height) Crop) CropToData(FitsImage fits)
    {
        int w = fits.Width, h = fits.Height, plane = w * h;
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!float.IsNaN(fits.Data[y * w + x])) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0 || (x0 == 0 && y0 == 0 && x1 == w - 1 && y1 == h - 1)) return (fits.Data, w, h, (0, 0, 1, 1));
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var data = new float[cw * ch * fits.Channels];
        for (int c = 0; c < fits.Channels; c++)
            for (int y = 0; y < ch; y++) Array.Copy(fits.Data, c * plane + (y0 + y) * w + x0, data, c * cw * ch + y * cw, cw);
        return (data, cw, ch, ((double)x0 / w, (double)y0 / h, (double)cw / w, (double)ch / h));
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    public ValueTask DisposeAsync() { _commands.Dispose(); return ValueTask.CompletedTask; }
}
