using ELink.Automation;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests.Automation;

/// <summary>Real frames, when there are some: set ELINK_REAL_LIGHTS to a folder of FITS lights (and ELINK_REAL_OUT to where the pictures go).
/// They are stacked by their stars alone (no solver) and made into a picture. Without the variable the tests do nothing.</summary>
public class RealDataTests(ITestOutputHelper output)
{
    private static string? Folder => Environment.GetEnvironmentVariable("ELINK_REAL_LIGHTS");

    [Fact]
    public async Task RealLightsStackByTheirStarsAndMakeAPicture()
    {
        if (Folder is not { } folder || !Directory.Exists(folder)) return;
        int count = int.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_COUNT"), out var c) ? c : 10;
        string outDir = Environment.GetEnvironmentVariable("ELINK_REAL_OUT") ?? Path.Combine(Path.GetTempPath(), "elink-real");
        Directory.CreateDirectory(outDir);
        int stride = int.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_STRIDE"), out var st) ? Math.Max(1, st) : 1;
        int skip = int.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_SKIP"), out var sk) ? sk : 0;
        var files = Directory.GetFiles(folder, "*.fits").Order().Skip(skip).Where((_, i) => i % stride == 0).Take(count).ToList();
        Assert.NotEmpty(files);
        var first = ELink.Imaging.FitsImage.Parse(File.ReadAllBytes(files[0]));
        double scale = first.GetDouble("SCALE", 2.4);
        double ra = first.GetDouble("RA", 0) / 15, dec = first.GetDouble("DEC", 0);
        double widthDeg = first.Width * scale / 3600, heightDeg = first.Height * scale / 3600;
        output.WriteLine($"{files.Count} frames, {first.Width}x{first.Height} at {scale}\"/px, centre {ra:0.000}h {dec:0.00}°");

        using var node = ElinkNode.Create("RD-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        await using var stack = new LiveStackService(node); await stack.StartAsync();
        await using var processing = new ProcessingService(node, outDir); await processing.StartAsync();
        var request = new LiveStackRequest
        {
            Label = new DirectoryInfo(folder).Parent?.Name ?? "real", Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
            FovWidthDegrees = widthDeg * 1.05, FovHeightDegrees = heightDeg * 1.05, PixelScaleArcsec = scale * 2, Debayer = "SuperPixel", Interpolation = "Bilinear",
            Registration = "Auto", FramePixelScaleArcsec = scale, Calibrate = false, SolveTimeoutSeconds = 1,
        };
        request.ShooterIds.Add("cam");
        // a field of your own choosing (degrees, and the pixel scale of the stack), and layers by pixel scale
        if (double.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_FOV"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fov))
        { request.FovWidthDegrees = fov; request.FovHeightDegrees = fov * 0.7; }
        if (double.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gridScale)) request.PixelScaleArcsec = gridScale;
        if (Environment.GetEnvironmentVariable("ELINK_REAL_LAYERS") == "1")
        {
            request.Layers.Add(new ImagingLayer { Label = "wide", MinScaleArcsec = 1.8 });
            request.Layers.Add(new ImagingLayer { Label = "detail", MaxScaleArcsec = 1.2 });
        }
        Assert.True((await Commands.CallAsync(node, LiveStackIds.Start, request)).Ok.Value);
        LiveStackState? state = null;
        await node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => { state = s; if (s.Message.Text != "") output.WriteLine(s.Message.Text); });
        // flats, when there are some: a master of the first few (the raw colour pattern kept), applied to the lights before they are stacked
        float[]? flat = null;
        if (Environment.GetEnvironmentVariable("ELINK_REAL_FLATS") is { } flatDir && Directory.Exists(flatDir))
        {
            var flats = Directory.GetFiles(flatDir, "*.fits").Order().Take(20).Select(f => ELink.Imaging.FitsImage.Parse(File.ReadAllBytes(f))).ToList();
            var mean = new float[flats[0].Data.Length];
            foreach (var fl in flats) for (int i = 0; i < mean.Length; i++) mean[i] += fl.Data[i] / flats.Count;
            var sample = Enumerable.Range(0, 100000).Select(i => mean[(long)i * mean.Length / 100000]).OrderBy(v => v).ToList();
            float median = sample[sample.Count / 2];
            for (int i = 0; i < mean.Length; i++) mean[i] /= median;
            flat = mean;
            output.WriteLine($"flat of {flats.Count} frames (median {median:0})");
        }
        int n = 0;
        foreach (var f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            if (flat is not null)
            {
                var light = ELink.Imaging.FitsImage.Parse(bytes);
                var cards = light.Header.Where(kv => kv.Key is not ("SIMPLE" or "BITPIX" or "NAXIS" or "NAXIS1" or "NAXIS2" or "NAXIS3" or "BZERO" or "BSCALE" or "EXTEND" or "END" or "COMMENT" or "HISTORY")).Select(kv => (kv.Key, kv.Value));
                bytes = ELink.Imaging.FitsImage.WriteFloat32(light.Width, light.Height, ELink.Imaging.Calibrate.Apply(light.Data, null, flat), cards);
            }
            await node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
            {
                Shooter = "cam", Format = ".fits", ExposureSeconds = first.GetDouble("EXPTIME", 120), FrameType = "Light", Timestamp = "r" + ++n,
                PointingRaHours = double.NaN, PointingDecDegrees = double.NaN, Data = new RawBytes(bytes),
            });
            for (int i = 0; i < 3000 && (state is null || state.FramesPending.Value > 0 || state.FramesStacked.Value + state.FramesRejected.Value < n); i++) await Task.Delay(100);   // one at a time: memory
        }
        output.WriteLine($"stacked {state!.FramesStacked.Value}, rejected {state.FramesRejected.Value}");
        Assert.True(state.FramesStacked.Value >= files.Count - 1, state.Message.Text);

        var picture = Assert.Single((await node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest { MaxWidth = 2400, MaxHeight = 1600 }, TimeSpan.FromMinutes(5)))!);
        Assert.True(picture.Ok.Value, picture.Message.Text);
        File.WriteAllBytes(Path.Combine(outDir, "picture.png"), picture.Png.Data);
        output.WriteLine($"{picture.Width.Value}x{picture.Height.Value}: {picture.Note.Text}");
        var plain = Assert.Single((await node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest
            { MaxWidth = 2400, MaxHeight = 1600, Settings = new ProcessingSettings { RemoveGradient = false, NeutralizeBackground = false } }, TimeSpan.FromMinutes(5)))!);
        File.WriteAllBytes(Path.Combine(outDir, "picture-no-processing.png"), plain.Png.Data);
        var export = Assert.Single((await node.CallFunctionAsync<ExportRequest, ExportResult>(ProcessingIds.Export, new ExportRequest { PictureToo = false }, TimeSpan.FromMinutes(5)))!);
        output.WriteLine(export.Message.Text);
    }

    /// <summary>How the frame grader sees real frames (raw colour frames, as a scope would grade them): stars, size, roundness, sky.</summary>
    [Fact]
    public void TheGraderOnRealFrames()
    {
        if (Folder is not { } folder || !Directory.Exists(folder)) return;
        int count = int.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_COUNT"), out var c) ? c : 10;
        int stride = int.TryParse(Environment.GetEnvironmentVariable("ELINK_REAL_STRIDE"), out var st) ? Math.Max(1, st) : 1;
        var grader = new ELink.Imaging.FrameGrader();
        foreach (var f in Directory.GetFiles(folder, "*.fits").Order().Where((_, i) => i % stride == 0).Take(count))
        {
            var img = ELink.Imaging.FitsImage.Parse(File.ReadAllBytes(f));
            var m = ELink.Imaging.FrameMetrics.Measure(img);
            var (ok, why) = grader.Grade(m);
            output.WriteLine($"{Path.GetFileName(f)}: stars {m.Stars,3}  hfr {m.Hfr,5:0.00}  elong {m.Elongation,4:0.00}  sky {m.Background,6:0}  {(ok ? "good" : "REJECT: " + why)}");
        }
    }
}
