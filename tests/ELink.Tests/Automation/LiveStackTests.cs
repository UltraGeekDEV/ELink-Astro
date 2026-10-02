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

public class LiveStackTests : IAsyncLifetime
{
    private const double RA = 83.8, DEC = -5.4;
    private TypeSafeEVentNode _node = null!;
    private LiveStackService _svc = null!;
    private LiveStackState? _last;
    private int _shots;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("LS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _svc = new LiveStackService(_node); await _svc.StartAsync();
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => _last = s);
    }

    public async Task DisposeAsync() { await _svc.DisposeAsync(); _node.Dispose(); }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static double Sky(double ra, double dec)
    {
        double dx = (ra - RA) * Math.Cos(DEC * Math.PI / 180) * 3600, dy = (dec - DEC) * 3600;
        double g(double cx, double cy, double s) => Math.Exp(-((dx - cx) * (dx - cx) + (dy - cy) * (dy - cy)) / (2 * s * s));
        return 1000 + 0.05 * dy + 8000 * g(0, 0, 80) + 5000 * g(-400, 250, 60);
    }

    /// <summary>A 16-bit frame of the sky through <paramref name="wcs"/>, plus a sky level offset, with or without its WCS in the header.</summary>
    private static byte[] Frame(TanWcs wcs, int w, int h, double skyOffset = 0, bool withWcs = true)
    {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var (ra, dec) = wcs.PixelToSky(x, y); px[y * w + x] = (ushort)Math.Clamp(Math.Round(Sky(ra, dec) + skyOffset), 0, 65535); }
        var cards = new Dictionary<string, string> { ["EXPTIME"] = "30" };
        if (withWcs) foreach (var (k, v) in wcs.Cards()) cards[k] = v;
        return FitsImage.Write16(w, h, px, cards);
    }

    private Task Shoot(string shooter, byte[] fits, double raH = double.NaN, double dec = double.NaN) =>
        _node.FireEventAsync(ShooterIds.Shot(shooter), new ShotEvent
        {
            Shooter = shooter, Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = $"t{Interlocked.Increment(ref _shots)}",
            PointingRaHours = raH, PointingDecDegrees = dec, Data = new RawBytes(fits),
        });

    private static LiveStackRequest Request(double scale, string registration = "Auto", double frameScale = 0, params string[] shooters)
    {
        var r = new LiveStackRequest
        {
            Label = "M42 test", Center = new SkyTarget { RaHours = RA / 15, DecDegrees = DEC, Epoch = "J2000" },
            FovWidthDegrees = 0.5, FovHeightDegrees = 0.4, PositionAngleDegrees = 15, PixelScaleArcsec = scale,
            Registration = registration, FramePixelScaleArcsec = frameScale,
        };
        foreach (var s in shooters.Length == 0 ? ["cam"] : shooters) r.ShooterIds.Add(s);
        return r;
    }

    private async Task<(FitsImage Img, TanWcs Wcs)> Image(int max = 0)
    {
        var answers = await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage,
            new LiveStackImageRequest { MaxWidth = max, MaxHeight = max }, TimeSpan.FromSeconds(10));
        var a = Assert.Single(answers!);
        Assert.True(a.Ok.Value, a.Message.Text);
        var img = FitsImage.Parse(a.Image.Data);
        Assert.Equal(a.Width.Value, img.Width);
        return (img, TanWcs.FromHeader(img.Header)!);
    }

    /// <summary>RMS against the true sky; with <paramref name="within"/>, only where that frame (w x h) covered the field.</summary>
    private static double Rms(FitsImage img, TanWcs wcs, TanWcs? within = null, int w = 0, int h = 0)
    {
        double e = 0; int n = 0;
        for (int y = 4; y < img.Height - 4; y += 2)
            for (int x = 4; x < img.Width - 4; x += 2)
            {
                var (ra, dec) = wcs.PixelToSky(x, y);
                if (within is not null)
                {
                    var (fx, fy) = within.SkyToPixel(ra, dec);
                    if (!(fx > 3 && fy > 3 && fx < w - 4 && fy < h - 4)) continue;
                }
                e += Math.Pow(img.Data[y * img.Width + x] - Sky(ra, dec), 2); n++;
            }
        return Math.Sqrt(e / n);
    }

    [Theory]
    [InlineData(1.5)]    // finer than the 4"/px frames: interpolated
    [InlineData(9.0)]    // coarser: binned and area-averaged
    public async Task RotatedOffsetFramesBuildTheRequestedField(double scale)
    {
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(scale))).Ok.Value);
        int w = 360, h = 270;
        // three frames that each cover part of the field, at different rotations, with different sky levels
        await Shoot("cam", Frame(TanWcs.Centered(RA, DEC, 0, 4, w, h), w, h));
        await Shoot("cam", Frame(TanWcs.Centered(RA + 0.12, DEC + 0.08, 40, 4, w, h), w, h, skyOffset: 300));
        await Shoot("cam", Frame(TanWcs.Centered(RA - 0.13, DEC - 0.07, -95, 4, w, h), w, h, skyOffset: -200));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 3, FramesPending.Value: 0 }), $"{_last?.FramesStacked.Value} {_last?.Message.Text}");
        Assert.Equal(0, _last!.FramesRejected.Value);
        Assert.Equal((int)Math.Round(0.5 * 3600 / scale), _last.Width.Value);
        Assert.Equal((int)Math.Round(0.4 * 3600 / scale), _last.Height.Value);
        Assert.Equal(90, _last.TotalExposureSeconds.Value, 3);
        Assert.Contains(scale < 4 ? "interpolated" : "area-averaged", _last.Message.Text);

        var (img, wcs) = await Image();
        Assert.Equal(scale, wcs.PixelScaleArcsec, 6);
        Assert.Equal(_last.Width.Value, img.Width);
        // background levels were matched to the first frame, so the whole field is the sky (to 16-bit rounding and edges)
        double rms = Rms(img, wcs);
        Assert.True(rms < 40, $"rms {rms:0.0} on a field peaking at ~9000");

        var (small, swcs) = await Image(max: 50);
        Assert.True(small.Width <= 50 && small.Height <= 50);
        Assert.True(swcs.PixelScaleArcsec > scale * 2);
    }

    [Fact]
    public async Task ScaleZeroTakesTheFirstFramesScale()
    {
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(0))).Ok.Value);
        Assert.Equal(0, _last!.Width.Value);
        await Shoot("cam", Frame(TanWcs.Centered(RA, DEC, 0, 6, 200, 150), 200, 150));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }), _last?.Message.Text);
        Assert.Equal(6, _last!.PixelScaleArcsec.Value, 6);
        Assert.Equal(300, _last.Width.Value);
    }

    [Fact]
    public async Task PointingRegistrationUsesTheShotsPointingAndTheGivenScale()
    {
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(5, "Pointing", frameScale: 4))).Ok.Value);
        int w = 300, h = 220;
        var at = TanWcs.Centered(RA + 0.05, DEC, 0, 4, w, h);
        await Shoot("cam", Frame(at, w, h, withWcs: false), (RA + 0.05) / 15, DEC);
        await Shoot("cam", Frame(at, w, h, withWcs: false));                        // no pointing: rejected
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1, FramesRejected.Value: 1 }), _last?.Message.Text);
        Assert.Contains("no pointing", _last!.Message.Text);
        var (img, wcs) = await Image();
        Assert.True(Rms(img, wcs, at, w, h) < 40);
    }

    [Fact]
    public async Task SolveRegistrationUsesTheSolversWcs()
    {
        int w = 300, h = 220;
        var truth = TanWcs.Centered(RA - 0.03, DEC + 0.02, 63, 3.3, w, h);
        int solves = 0;
        using var solver = new CommandSet(_node);
        await solver.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, r =>
        {
            Interlocked.Increment(ref solves);
            Assert.InRange(r.HintRaHours.Value * 15, RA - 0.1, RA);                // the shot's pointing is the hint
            return Task.FromResult(new SolveResult
            {
                Solved = true, RaHours = truth.CrVal1 / 15, DecDegrees = truth.CrVal2, PixelScale = truth.PixelScaleArcsec, HasWcs = true,
                WcsCrVal1 = truth.CrVal1, WcsCrVal2 = truth.CrVal2, WcsCrPix1 = truth.CrPix1 + 1, WcsCrPix2 = truth.CrPix2 + 1,
                WcsCd11 = truth.Cd11, WcsCd12 = truth.Cd12, WcsCd21 = truth.Cd21, WcsCd22 = truth.Cd22,
            });
        }, "fake solver");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(4, "Solve"))).Ok.Value);
        await Shoot("cam", Frame(truth, w, h, withWcs: false), (RA - 0.02) / 15, DEC);
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }), _last?.Message.Text);
        Assert.Equal(1, solves);
        Assert.Contains("solved", _last!.Message.Text);
        var (img, wcs) = await Image();
        Assert.True(Rms(img, wcs, truth, w, h) < 40);
    }

    [Fact]
    public async Task ValidatesAndCanBeResetAndStopped()
    {
        var huge = Request(0.2); huge.FovWidthDegrees = 5; huge.FovHeightDegrees = 5;
        var r = await Commands.CallAsync(_node, LiveStackIds.Start, huge);
        Assert.False(r.Ok.Value); Assert.Contains("megapixels", r.Error.Text);
        Assert.False((await Commands.CallAsync(_node, LiveStackIds.Start, Request(2, "Pointing"))).Ok.Value);   // pointing without frame scale
        var bad = Request(2); bad.Interpolation = "Lanczos";
        Assert.False((await Commands.CallAsync(_node, LiveStackIds.Start, bad)).Ok.Value);

        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(8))).Ok.Value);
        await Shoot("cam", Frame(TanWcs.Centered(RA, DEC, 0, 4, 200, 150), 200, 150));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }));
        // a frame from the far side of the sky misses the field
        await Shoot("cam", Frame(TanWcs.Centered(RA + 180, -DEC, 0, 4, 200, 150), 200, 150));
        Assert.True(await Eventually(() => _last is { FramesRejected.Value: 1 }), _last?.Message.Text);

        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Reset, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 0, FramesRejected.Value: 0 }));

        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Stop, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Stopped" }));
        await Shoot("cam", Frame(TanWcs.Centered(RA, DEC, 0, 4, 200, 150), 200, 150));
        await Task.Delay(500);
        Assert.Equal(0, _last!.FramesStacked.Value);                                 // no longer listening

        // frames can also be pushed in directly
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(8))).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Add, new LiveStackFrame { Image = new RawBytes(Frame(TanWcs.Centered(RA, DEC, 0, 4, 200, 150), 200, 150)), Source = "disk" })).Ok.Value);
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1, LastFrame.Text: "disk" }), _last?.Message.Text);
    }

    /// <summary>A raw one-shot-colour frame: the sky with R = sky, G = 0.6 sky, B = 0.3 sky, through a Bayer filter.</summary>
    private static byte[] BayerFrame(TanWcs wcs, int w, int h, string pattern, bool writePattern = true)
    {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (ra, dec) = wcs.PixelToSky(x, y);
                double k = pattern[(y % 2) * 2 + x % 2] switch { 'R' => 1.0, 'G' => 0.6, _ => 0.3 };
                px[y * w + x] = (ushort)Math.Round(Sky(ra, dec) * k);
            }
        var cards = new Dictionary<string, string> { ["EXPTIME"] = "30" };
        if (writePattern) cards["BAYERPAT"] = $"'{pattern}'";
        foreach (var (k, v) in wcs.Cards()) cards[k] = v;
        return FitsImage.Write16(w, h, px, cards);
    }

    [Theory]
    [InlineData("Interpolated", 4.0)]
    [InlineData("SuperPixel", 8.0)]    // half the resolution: the stack takes the doubled scale
    public async Task RawColourFramesAreDebayeredIntoAColourStack(string mode, double expectedScale)
    {
        var req = Request(0); req.Debayer = mode;
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, req)).Ok.Value);
        int w = 360, h = 270;
        var at = TanWcs.Centered(RA, DEC, 25, 4, w, h);
        await Shoot("cam", BayerFrame(at, w, h, "GBRG"));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }), _last?.Message.Text);
        Assert.Equal(3, _last!.Channels.Value);
        Assert.Equal(expectedScale, _last.PixelScaleArcsec.Value, 6);
        Assert.Contains("GBRG", _last.Message.Text);

        var (img, wcs) = await Image();
        Assert.Equal(3, img.Channels);
        int plane = img.Width * img.Height;
        // colour ratios at the bright centre survive, and the picture is where it should be
        var (cx, cy) = wcs.SkyToPixel(RA, DEC);
        int i = (int)Math.Round(cy) * img.Width + (int)Math.Round(cx);
        double r = img.Data[i], g = img.Data[plane + i], b = img.Data[2 * plane + i];
        Assert.InRange(r, Sky(RA, DEC) * 0.97, Sky(RA, DEC) * 1.03);
        Assert.Equal(0.6, g / r, 2); Assert.Equal(0.3, b / r, 2);
    }

    [Fact]
    public async Task APatternCanBeGivenAndDebayeringSwitchedOff()
    {
        int w = 200, h = 150;
        var at = TanWcs.Centered(RA, DEC, 0, 4, w, h);
        var req = Request(4); req.BayerPattern = "BGGR";                       // the frames do not say
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, req)).Ok.Value);
        await Shoot("cam", BayerFrame(at, w, h, "BGGR", writePattern: false));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }), _last?.Message.Text);
        Assert.Equal(3, _last!.Channels.Value);
        var (img, wcs) = await Image();
        var (cx, cy) = wcs.SkyToPixel(RA, DEC);
        int i = (int)Math.Round(cy) * img.Width + (int)Math.Round(cx), plane = img.Width * img.Height;
        Assert.Equal(0.3, img.Data[2 * plane + i] / img.Data[i], 2);           // blue really is blue

        var off = Request(4); off.Debayer = "None";
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, off)).Ok.Value);
        await Shoot("cam", BayerFrame(at, w, h, "RGGB"));
        Assert.True(await Eventually(() => _last is { FramesStacked.Value: 1 }), _last?.Message.Text);
        Assert.Equal(1, _last!.Channels.Value);
        Assert.Contains("kept raw", _last.Message.Text);

        var bad = Request(4); bad.Debayer = "Fancy";
        Assert.False((await Commands.CallAsync(_node, LiveStackIds.Start, bad)).Ok.Value);
        bad = Request(4); bad.BayerPattern = "RGBG";
        Assert.False((await Commands.CallAsync(_node, LiveStackIds.Start, bad)).Ok.Value);
    }

    [Fact]
    public async Task FramesTheScopeRejectedAreNotStacked()
    {
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(8))).Ok.Value);
        var fits = Frame(TanWcs.Centered(RA, DEC, 0, 4, 200, 150), 200, 150);
        await _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
        {
            Shooter = "cam", Format = ".fits", FrameType = "Light", Timestamp = "t-bad", Data = new RawBytes(fits), Quality = "Rejected", QualityNote = "clouds",
        });
        Assert.True(await Eventually(() => _last is { FramesRejected.Value: 1 }), _last?.Message.Text);
        Assert.Equal(0, _last!.FramesStacked.Value);
        Assert.Contains("clouds", _last.Message.Text);
    }

    [Fact]
    public async Task AFrameRelayedByAScopeIsStackedOnce()
    {
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, Request(8, "Auto", 0, "cam", "scope"))).Ok.Value);
        var fits = Frame(TanWcs.Centered(RA, DEC, 0, 4, 200, 150), 200, 150);
        var shot = new ShotEvent { Shooter = "cam", Format = ".fits", FrameType = "Light", Timestamp = "t-same", Data = new RawBytes(fits) };
        await _node.FireEventAsync(ShooterIds.Shot("cam"), shot);
        await _node.FireEventAsync(ShooterIds.Shot("scope"), shot);
        await Task.Delay(800);
        Assert.Equal(1, _last!.FramesStacked.Value);
    }
}
