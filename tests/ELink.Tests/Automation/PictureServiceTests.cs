using ELink.Automation;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The processing service: a processed picture of the live stack, the linear data saved beside it, and the live stack telling where each frame landed.</summary>
public class PictureServiceTests : IAsyncLifetime
{
    private const int W = 120, H = 90;
    private TypeSafeEVentNode _node = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elink-pic-" + Guid.NewGuid().ToString("N"));
    private int _shots;

    public Task InitializeAsync() { _node = ElinkNode.Create("PS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next()); return Task.CompletedTask; }
    public Task DisposeAsync() { _node.Dispose(); try { Directory.Delete(_dir, true); } catch { } return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private Task Shoot()
    {
        var wcs = TanWcs.Centered(84, 10, 0, 2, W, H);
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = (ushort)Math.Round(1000 + 4 * x + 6 * Math.Exp(-((x - 60) * (x - 60) + (y - 45) * (y - 45)) / 8.0) * 1000 / 6 + (x * 7 + y * 13 + _shots) % 9);
        var cards = new Dictionary<string, string> { ["EXPTIME"] = "30" };
        foreach (var (k, v) in wcs.Cards()) cards[k] = v;
        return _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
        {
            Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = "t" + ++_shots, Data = new RawBytes(FitsImage.Write16(W, H, px, cards)),
        });
    }

    [Fact]
    public async Task APictureOfTheStackIsMadeAndSavedBesideTheLinearData()
    {
        await using var stack = new LiveStackService(_node, _dir); await stack.StartAsync();
        await using var processing = new ProcessingService(_node, Path.Combine(_dir, "pictures")); await processing.StartAsync();
        var request = new LiveStackRequest
        {
            Label = "Field", Center = new SkyTarget { RaHours = 84 / 15.0, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = W * 2 / 3600.0, FovHeightDegrees = H * 2 / 3600.0, PixelScaleArcsec = 2,
            Interpolation = "Nearest", SessionKey = "field",
        };
        request.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        var added = new List<StackFrameAdded>();
        await _node.HookEventAsync(LiveStackIds.FrameAdded, (StackFrameAdded e) => { lock (added) added.Add(e); });
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);

        var nothing = Assert.Single((await _node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest(), TimeSpan.FromSeconds(20)))!);
        Assert.False(nothing.Ok.Value);
        Assert.Contains("nothing stacked", nothing.Message.Text);

        await Shoot(); await Shoot();
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 2), state?.Message.Text);
        // where each frame went: the whole grid
        Assert.True(await Eventually(() => { lock (added) return added.Count == 2; }));
        StackFrameAdded last; lock (added) last = added[^1];
        var xs = new[] { last.X0.Value, last.X1.Value, last.X2.Value, last.X3.Value }; var ys = new[] { last.Y0.Value, last.Y1.Value, last.Y2.Value, last.Y3.Value };
        Assert.InRange(xs.Min(), -0.02, 0.02); Assert.InRange(xs.Max(), 0.98, 1.02); Assert.InRange(ys.Min(), -0.02, 0.02); Assert.InRange(ys.Max(), 0.98, 1.02);
        Assert.Equal(2, last.Frames.Value);

        var settings = new ProcessingSettings();
        var picture = Assert.Single((await _node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest { Settings = settings, MaxWidth = 60, MaxHeight = 60 }, TimeSpan.FromSeconds(30)))!);
        Assert.True(picture.Ok.Value, picture.Message.Text);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, picture.Png.Data[..4]);
        Assert.True(picture.Width.Value <= 60 && picture.Height.Value <= 60);
        Assert.Equal(2, picture.Frames.Value); Assert.Equal(128, picture.Histogram.Data.Length);
        Assert.True(picture.FlipY.Value);                                    // FITS rows run from the bottom
        Assert.True(picture.GradientPercent.Value > 0.5, $"the gradient (4 ADU a pixel) is seen: {picture.GradientPercent.Value}");
        Assert.Contains("gradient", picture.Note.Text);
        var plain = Assert.Single((await _node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest
            { Settings = new ProcessingSettings { RemoveGradient = false, Stretch = false } }, TimeSpan.FromSeconds(30)))!);
        Assert.DoesNotContain("gradient", plain.Note.Text);

        // saved: the linear FITS is there whatever the picture
        var export = Assert.Single((await _node.CallFunctionAsync<ExportRequest, ExportResult>(ProcessingIds.Export, new ExportRequest { Settings = settings, Label = "My Field" }, TimeSpan.FromSeconds(60)))!);
        Assert.True(export.Ok.Value, export.Message.Text);
        var files = export.Files.Select(f => f.Text).ToList();
        string linear = Assert.Single(files, f => f.EndsWith("_linear.fits")), png = Assert.Single(files, f => f.EndsWith("_picture.png"));
        Assert.StartsWith("My_Field_", Path.GetFileName(linear));
        var fits = FitsImage.Parse(File.ReadAllBytes(linear));
        Assert.Equal((W, H), (fits.Width, fits.Height));
        Assert.True(fits.Data.Max() > 1500 && fits.Data.Min() >= 0);          // unstretched: ADU values, not 0..1
        var bytes = File.ReadAllBytes(png);
        Assert.Equal(16, bytes[24]);                                         // IHDR bit depth
        Assert.True(File.Exists(Path.ChangeExtension(png, ".txt")));
        var linearOnly = Assert.Single((await _node.CallFunctionAsync<ExportRequest, ExportResult>(ProcessingIds.Export, new ExportRequest { PictureToo = false, Label = "raw" }, TimeSpan.FromSeconds(60)))!);
        Assert.Single(linearOnly.Files);

        // and the kept stack has its linear picture beside it
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Save, NOTESVoid.Void)).Ok.Value);
        Assert.True(File.Exists(Path.Combine(_dir, "stacks", "field", "linear.fits")));
    }
}
