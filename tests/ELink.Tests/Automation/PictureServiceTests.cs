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

    [Fact]
    public async Task ThePictureIsCutToTheStackPartThatHasDataAndSaysWhere()
    {
        await using var stack = new LiveStackService(_node); await stack.StartAsync();
        await using var processing = new ProcessingService(_node, Path.Combine(_dir, "pictures")); await processing.StartAsync();
        // a grid twice as wide and high as the frame: the frame fills the middle of it
        var request = new LiveStackRequest
        {
            Label = "Big", Center = new SkyTarget { RaHours = 84 / 15.0, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = 2 * W * 2 / 3600.0, FovHeightDegrees = 2 * H * 2 / 3600.0, PixelScaleArcsec = 2, Interpolation = "Nearest",
        };
        request.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);
        await Shoot();
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 1), state?.Message.Text);
        var picture = Assert.Single((await _node.CallFunctionAsync<ProcessRequest, ProcessedImage>(ProcessingIds.Process, new ProcessRequest(), TimeSpan.FromSeconds(30)))!);
        Assert.True(picture.Ok.Value, picture.Message.Text);
        Assert.InRange(picture.Width.Value, W - 3, W + 3); Assert.InRange(picture.Height.Value, H - 3, H + 3);
        Assert.InRange(picture.CropWidth.Value, 0.45, 0.55); Assert.InRange(picture.CropHeight.Value, 0.45, 0.55);
        Assert.InRange(picture.CropLeft.Value, 0.2, 0.3); Assert.InRange(picture.CropTop.Value, 0.2, 0.3);
    }

    [Fact]
    public async Task FramesWithOnlyANominalWcsAreLinedUpByTheirStars()
    {
        // INDI and other capture programs write CDELT/CROTA from the mount's position, the same for every frame, whatever the sky did
        await using var stack = new LiveStackService(_node); await stack.StartAsync();
        const int FW = 220, FH = 160;
        var request = new LiveStackRequest
        {
            Label = "Nominal", Center = new SkyTarget { RaHours = 84 / 15.0, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = FW * 2 * 1.4 / 3600, FovHeightDegrees = FH * 2 * 1.4 / 3600, PixelScaleArcsec = 2,
            Interpolation = "Bilinear", FramePixelScaleArcsec = 2, Registration = "Auto", SolveTimeoutSeconds = 1,
        };
        request.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        var messages = new List<string>(); LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState st) => { state = st; lock (messages) messages.Add(st.Message.Text); });
        var rnd = new Random(3);
        var stars = Enumerable.Range(0, 45).Select(_ => (X: 15 + rnd.NextDouble() * (FW - 30), Y: 15 + rnd.NextDouble() * (FH - 30), F: 4000 + rnd.NextDouble() * 20000)).ToArray();
        var cards = new Dictionary<string, string>
        {
            ["EXPTIME"] = "30", ["CTYPE1"] = "'RA---TAN'", ["CTYPE2"] = "'DEC--TAN'", ["CRVAL1"] = "84", ["CRVAL2"] = "10", ["CRPIX1"] = (FW / 2).ToString(), ["CRPIX2"] = (FH / 2).ToString(),
            ["CDELT1"] = "-0.000555556", ["CDELT2"] = "0.000555556", ["CROTA2"] = "30",
        };
        float Peak(byte[] fits) => FitsImage.Parse(fits).Data.Max();
        byte[] Make(double dx, double dy, int seed)
        {
            var noise = new Random(seed); var px = new ushort[FW * FH];
            for (int y = 0; y < FH; y++)
                for (int x = 0; x < FW; x++)
                {
                    double v = 1500 + (noise.NextDouble() - 0.5) * 60;
                    foreach (var st in stars) { double ddx = x - (st.X + dx), ddy = y - (st.Y + dy); if (Math.Abs(ddx) < 9 && Math.Abs(ddy) < 9) v += st.F * Math.Exp(-(ddx * ddx + ddy * ddy) / (2 * 1.6 * 1.6)); }
                    px[y * FW + x] = (ushort)Math.Clamp(v, 0, 65535);
                }
            return FitsImage.Write16(FW, FH, px, cards);
        }
        var offsets = new[] { (0.0, 0.0), (7.0, -4.0), (-5.0, 9.0), (3.0, 3.0), (-8.0, -6.0) };
        float single = Peak(Make(0, 0, 1));
        int n = 0;
        foreach (var (dx, dy) in offsets)
        {
            await _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
            {
                Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = "n" + ++n, Data = new RawBytes(Make(dx, dy, n)),
            });
            Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value + state.FramesRejected.Value == n && state.FramesPending.Value == 0), state?.Message.Text);
        }
        Assert.Equal(5, state!.FramesStacked.Value);
        lock (messages) { Assert.Contains(messages, m => m.Contains("reference frame")); Assert.Contains(messages, m => m.Contains("stars,") && m.Contains("matched")); }
        // the stars are sharp: lined up, the brightest pixel of the stack is as bright as in one frame (misplaced, it would be a fifth of it)
        var image = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Neutralize = false }, TimeSpan.FromSeconds(10)))!);
        float stacked = Peak(image.Image.Data);
        Assert.True(stacked > 0.75 * single, $"stack peak {stacked} against one frame's {single}");
        Assert.True(LiveStackService.IsSolvedWcs(new Dictionary<string, string> { ["CD1_1"] = "1" }));
        Assert.False(LiveStackService.IsSolvedWcs(cards));
    }

    [Fact]
    public async Task AFrameOfAnotherScaleFromAnotherTelescopeIsPlacedOnTheFirstByItsStars()
    {
        await using var stack = new LiveStackService(_node); await stack.StartAsync();
        const int FW = 300, FH = 220;
        var request = new LiveStackRequest
        {
            Label = "Mixed", Center = new SkyTarget { RaHours = 84 / 15.0, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = FW * 2 / 3600.0, FovHeightDegrees = FH * 2 / 3600.0, PixelScaleArcsec = 2,
            Interpolation = "Nearest", Registration = "Auto", SolveTimeoutSeconds = 1,
        };
        request.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        var messages = new List<string>(); LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState st) => { state = st; lock (messages) messages.Add(st.Message.Text); });
        var rnd = new Random(8);
        // the sky in the first frame's pixels (2"/px); one star far brighter than the rest
        var stars = Enumerable.Range(0, 140).Select(_ => (X: 10 + rnd.NextDouble() * (FW - 20), Y: 10 + rnd.NextDouble() * (FH - 20), F: 3000 + rnd.NextDouble() * 9000)).ToList();
        stars.Add((FW * 0.5 + 23, FH * 0.5 - 17, 60000));
        byte[] Make(double zoom, int seed)
        {
            // zoom 2: the second telescope sees 1"/px: the middle of the first's field, twice as big
            var cards = new Dictionary<string, string>
            {
                ["EXPTIME"] = "30", ["CTYPE1"] = "'RA---TAN'", ["CTYPE2"] = "'DEC--TAN'", ["CRVAL1"] = "84", ["CRVAL2"] = "10", ["CRPIX1"] = (FW / 2).ToString(), ["CRPIX2"] = (FH / 2).ToString(),
                ["CDELT1"] = "-0.000555556", ["CDELT2"] = "0.000555556", ["CROTA2"] = "0", ["SCALE"] = (2.0 / zoom).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            var noise = new Random(seed); var px = new ushort[FW * FH];
            for (int y = 0; y < FH; y++)
                for (int x = 0; x < FW; x++)
                {
                    double sx = (x - FW / 2.0) / zoom + FW / 2.0, sy = (y - FH / 2.0) / zoom + FH / 2.0;      // where this pixel is on the first frame
                    double v = 1500 + (noise.NextDouble() - 0.5) * 60;
                    foreach (var st in stars) { double dx = sx - st.X, dy = sy - st.Y; if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8) v += st.F * Math.Exp(-(dx * dx + dy * dy) / (2 * 1.6 * 1.6)); }
                    px[y * FW + x] = (ushort)Math.Clamp(v, 0, 65535);
                }
            return FitsImage.Write16(FW, FH, px, cards);
        }
        async Task Send(byte[] fits, int n)
        {
            await _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent { Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = "z" + n, Data = new RawBytes(fits) });
            Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value + state.FramesRejected.Value == n && state.FramesPending.Value == 0), state?.Message.Text);
        }
        (int X, int Y) Brightest(byte[] fits) { var f = FitsImage.Parse(fits); int best = 0; for (int i = 0; i < f.Width * f.Height; i++) if (f.Data[i] > f.Data[best]) best = i; return (best % f.Width, best / f.Width); }
        async Task<(int X, int Y)> Where()
        {
            var img = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Neutralize = false }, TimeSpan.FromSeconds(10)))!);
            return Brightest(img.Image.Data);
        }
        await Send(Make(1, 1), 1);
        var first = await Where();
        await Send(Make(2, 2), 2);                      // 2x the scale, same field centre
        lock (messages) Assert.Contains(messages, m => m.Contains("stars,") && m.Contains("matched"));
        var after = await Where();
        Assert.InRange(Math.Abs(after.X - first.X), 0, 2); Assert.InRange(Math.Abs(after.Y - first.Y), 0, 2);   // the bright star is where it was, not smeared or shifted
    }

    [Fact]
    public async Task FramesOfTwoTelescopesAtOtherScalesAndAnglesAreStackedTogetherWithoutASolver()
    {
        // what the live pipeline gives: no WCS in the frames, each frame's pointing (a little off), the scale of its telescope and its camera angle (a little off)
        await using var stack = new LiveStackService(_node); await stack.StartAsync();
        const double Ra = 84.0, Dec = 10.0;
        var request = new LiveStackRequest
        {
            Label = "Together", Center = new SkyTarget { RaHours = Ra / 15, DecDegrees = Dec, Epoch = "J2000" }, FovWidthDegrees = 1100 * 4 / 3600.0 * 1.3, FovHeightDegrees = 800 * 4 / 3600.0 * 1.3,
            PixelScaleArcsec = 4, Interpolation = "Bilinear", Registration = "Auto", SolveTimeoutSeconds = 1,
        };
        request.ShooterIds.Add("a"); request.ShooterIds.Add("b");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        var messages = new List<string>(); LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState st) => { state = st; lock (messages) messages.Add(st.Message.Text); });

        var rnd = new Random(12);
        // stars on the sky, in degrees from the field centre (a patch 0.9 x 0.6 degrees); one far brighter than the rest
        var sky = Enumerable.Range(0, 400).Select(_ => (Ra: Ra + (rnd.NextDouble() - 0.5) * 0.9 / Math.Cos(Dec * Math.PI / 180), Dec: Dec + (rnd.NextDouble() - 0.5) * 0.6, F: 800 + Math.Pow(rnd.NextDouble(), 3) * 20000)).ToList();
        sky.Add((Ra + 0.02, Dec - 0.015, 90000));
        byte[] Render(TanWcs wcs, int w, int h, double fwhmArcsec, int seed)
        {
            var noise = new Random(seed); var px = new ushort[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = (ushort)(1200 + (noise.NextDouble() - 0.5) * 60);
            double sigma = fwhmArcsec / wcs.PixelScaleArcsec / 2.355;
            foreach (var st in sky)
            {
                var (x, y) = wcs.SkyToPixel(st.Ra, st.Dec);
                int r = (int)Math.Ceiling(5 * sigma);
                for (int yy = Math.Max(0, (int)y - r); yy <= Math.Min(h - 1, (int)y + r); yy++)
                    for (int xx = Math.Max(0, (int)x - r); xx <= Math.Min(w - 1, (int)x + r); xx++)
                        px[yy * w + xx] = (ushort)Math.Min(65535, px[yy * w + xx] + st.F * Math.Exp(-((xx - x) * (xx - x) + (yy - y) * (yy - y)) / (2 * sigma * sigma)));
            }
            return FitsImage.Write16(w, h, px, new Dictionary<string, string> { ["EXPTIME"] = "30" });
        }
        async Task Send(string shooter, byte[] fits, double scale, double trueAngle, double angleError, double raError, double decError, int n)
        {
            await _node.FireEventAsync(ShooterIds.Shot(shooter), new ShotEvent
            {
                Shooter = shooter, Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = "t" + n, Data = new RawBytes(fits),
                PointingRaHours = (Ra + raError) / 15, PointingDecDegrees = Dec + decError, PixelScaleArcsec = scale, CameraAngleDegrees = trueAngle + angleError,
            });
            Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value + state.FramesRejected.Value == n && state.FramesPending.Value == 0), state?.Message.Text);
        }
        (int X, int Y) Brightest(byte[] fits) { var f = FitsImage.Parse(fits); int best = 0; for (int i = 0; i < f.Width * f.Height; i++) if (f.Data[i] > f.Data[best]) best = i; return (best % f.Width, best / f.Width); }
        async Task<(int X, int Y)> Where()
        {
            var img = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Neutralize = false }, TimeSpan.FromSeconds(10)))!);
            return Brightest(img.Image.Data);
        }

        // telescope A: 4"/px, camera not turned; telescope B: 1.5"/px, camera turned 35°, its pointing 12" and its angle 2.5° out
        var wcsA = TanWcs.Centered(Ra, Dec, 0, 4, 1100, 800);
        await Send("a", Render(wcsA, 1100, 800, 9, 1), 4, 0, 0, 0, 0, 1);
        var first = await Where();
        var wcsB = TanWcs.Centered(Ra, Dec, 35, 1.5, 1400, 1000);
        await Send("b", Render(wcsB, 1400, 1000, 4.5, 2), 1.5, 35, 2.5, 12.0 / 3600, 0, 2);
        await Send("b", Render(wcsB with { CrPix1 = wcsB.CrPix1 + 15, CrPix2 = wcsB.CrPix2 - 9 }, 1400, 1000, 4.5, 3), 1.5, 35, 2.5, 12.0 / 3600, 0, 3);   // dithered
        await Send("a", Render(wcsA with { CrPix1 = wcsA.CrPix1 - 6, CrPix2 = wcsA.CrPix2 + 4 }, 1100, 800, 9, 4), 4, 0, 0, 0, 0, 4);
        Assert.Equal(4, state!.FramesStacked.Value);
        lock (messages)
        {
            Assert.Contains(messages, m => m.Contains("reference frame"));
            Assert.Equal(3, messages.Distinct().Count(m => m.Contains("stars,") && m.Contains("matched")));            // every later frame was placed by its stars
            Assert.DoesNotContain(messages, m => m.Contains("did not match"));
        }
        // the bright star stays where the first frame had it: the other telescope's frames landed on it
        var after = await Where();
        Assert.InRange(Math.Abs(after.X - first.X), 0, 2); Assert.InRange(Math.Abs(after.Y - first.Y), 0, 2);
    }
}
