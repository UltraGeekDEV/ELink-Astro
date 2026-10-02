using ELink.Automation;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using ELink.Imaging;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;
using ELink.Contracts.Equipment;
using Xunit;

namespace ELink.Tests.Automation;

public class CalibrationMathTests
{
    [Fact]
    public void DarkIsTakenOffAndFlatDividedOut()
    {
        float[] light = [110, 210, 60, 1000], dark = [10, 10, 10, 10], flat = [1, 2, 0.5f, 0.01f];
        var r = Calibrate.Apply(light, dark, flat);
        Assert.Equal([100f, 100f, 100f, 990f], r);   // the last pixel's flat is black: left undivided
        Assert.Equal(110f, light[0]);                 // the frame itself is untouched
        Assert.Equal([110f, 210f, 60f, 1000f], Calibrate.Apply(light, null, null));
    }
}

public class CalibrationTests : IAsyncLifetime
{
    private const int W = 120, H = 90;
    private TypeSafeEVentNode _node = null!;
    private CalibrationService _svc = null!;
    private FakeShooter _cam = null!;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "elink-cal-" + Guid.NewGuid().ToString("N"));
    private CalibrationState? _state;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("CAL-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _svc = new CalibrationService(_node, _data); await _svc.StartAsync();
        _cam = new FakeShooter(_node, "cam") { FrameMaker = Make }; await _cam.StartAsync();
        await _node.HookEventAsync(CalibrationIds.State, (CalibrationState s) => _state = s);
    }

    public async Task DisposeAsync() { await _cam.DisposeAsync(); await _svc.DisposeAsync(); _node.Dispose(); try { Directory.Delete(_data, true); } catch { } }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    /// <summary>The camera's dark signal: a glow from the left edge and a few hot pixels.</summary>
    private static double Dark(int x, int y) => 200 + 400.0 * x / W + (x % 37 == 5 && y % 23 == 7 ? 5000 : 0);
    /// <summary>Vignetting: the field falls off to 65% in the corners.</summary>
    private static double Vignette(int x, int y)
    {
        double cx = W / 2.0, cy = H / 2.0;
        return 1 - 0.35 * ((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (cx * cx + cy * cy);
    }

    /// <summary>The fake camera's frames: darks and biases are dark signal (each with a cosmic ray somewhere else), flats are an even light through the vignetting.</summary>
    private byte[] Make(int n)
    {
        var e = _cam.Requests[n - 1];
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double v = e.FrameType.Text == "Bias" ? 200 : Dark(x, y);
                if (e.FrameType.Text == "Flat") v += 200000 * e.Seconds.Value * Vignette(x, y);
                px[y * W + x] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
            }
        px[(n * 977) % px.Length] = 60000;   // a cosmic ray
        return FitsImage.Write16(W, H, px, new Dictionary<string, string> { ["EXPTIME"] = e.Seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), ["CCD-TEMP"] = "-10.2", ["GAIN"] = "120" });
    }

    private async Task<CalibrationMaster> Capture(string kind, double seconds, int count = 3, string filter = "")
    {
        var r = await Commands.CallAsync(_node, CalibrationIds.Capture, new CaptureRequest { ShooterId = "cam", Kind = kind, ExposureSeconds = seconds, Count = count, Filter = filter });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => _state?.Phase.Text is "Done" or "Error"), _state?.Message.Text);
        Assert.Equal("Done", _state!.Phase.Text);
        _state = null;
        var list = (await _node.CallFunctionAsync<NOTESVoid, CalibrationMasters>(CalibrationIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(5)))!.Single();
        return list.Masters.Last();
    }

    private MasterMatch Find(string kind, double seconds, string filter = "", double temp = -10) =>
        _svc.Find(new MasterQuery { ShooterId = "cam", Kind = kind, ExposureSeconds = seconds, Filter = filter, TemperatureC = temp, Gain = 120, Width = W, Height = H });

    [Fact]
    public async Task DarksCombineIntoAMasterWithoutCosmicRays()
    {
        var m = await Capture("Dark", 0.2);
        Assert.Equal("Dark", m.Kind.Text);
        Assert.Equal(-10.2, m.TemperatureC.Value, 3);
        Assert.Equal(120, m.Gain.Value, 3);
        Assert.Equal((W, H, 3), (m.Width.Value, m.Height.Value, m.Frames.Value));
        Assert.All(_cam.Requests, e => Assert.Equal("Dark", e.FrameType.Text));

        var match = Find("Dark", 0.2);
        Assert.True(match.Found.Value, match.Message.Text);
        var img = FitsImage.Parse(match.Image.Data);
        double worst = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) worst = Math.Max(worst, Math.Abs(img.Data[y * W + x] - Math.Round(Dark(x, y))));
        Assert.True(worst < 0.01, $"worst pixel off by {worst} (cosmic rays should be left out)");
    }

    [Fact]
    public async Task MatchingPrefersTheRightDarkThenABias()
    {
        await Capture("Dark", 0.2);
        await Capture("Bias", 0);
        Assert.Equal("Dark", Find("Dark", 0.2).Master.Kind.Text);
        Assert.Equal("Bias", Find("Dark", 30).Master.Kind.Text);              // no 30 s dark: the bias stands in
        Assert.Equal("Bias", Find("Dark", 0.2, temp: 5).Master.Kind.Text);    // the dark is from a colder sensor
        Assert.False(_svc.Find(new MasterQuery { ShooterId = "other", Kind = "Dark", ExposureSeconds = 0.2 }).Found.Value);
        Assert.False(_svc.Find(new MasterQuery { ShooterId = "cam", Kind = "Dark", ExposureSeconds = 0.2, Width = W * 2, Height = H }).Found.Value);
        Assert.False(Find("Flat", 0, "Ha").Found.Value);
    }

    [Fact]
    public async Task FlatsFindTheirExposureAndAreKeptAcrossRestarts()
    {
        await Capture("Dark", 0.2);
        var m = await Capture("Flat", 0, filter: "Ha");
        Assert.Equal("Ha", m.Filter.Text);
        // 200000 ADU/s through the vignetting: half of the range is ~0.16 s
        Assert.InRange(m.ExposureSeconds.Value, 0.12, 0.2);
        Assert.Contains(_cam.Requests, e => e.FrameType.Text == "Flat" && e.Filter.Text == "Ha");

        var flat = FitsImage.Parse(Find("Flat", 0, "Ha").Image.Data);
        double centre = flat.Data[(H / 2) * W + W / 2], corner = flat.Data[0];
        Assert.InRange(corner / centre, 0.64, 0.66);   // the dark was taken off, so the shape is exactly the vignetting

        await _svc.DisposeAsync();
        _svc = new CalibrationService(_node, _data); await _svc.StartAsync();
        Assert.True(Find("Flat", 0, "Ha").Found.Value);
        Assert.True((await Commands.CallAsync(_node, CalibrationIds.Delete, (BinaryConvertibleString)m.Id.Text)).Ok.Value);
        Assert.False(Find("Flat", 0, "Ha").Found.Value);
        Assert.True(Find("Dark", 0.2).Found.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LiveStackCalibratesFrames(bool calibrate)
    {
        await Capture("Dark", 0.2);
        await Capture("Flat", 0, filter: "L");
        await using var stack = new LiveStackService(_node); await stack.StartAsync();
        LiveStackState? last = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => last = s);
        const double ra = 83.8, dec = -5.4;
        var wcs = TanWcs.Centered(ra, dec, 0, 4, W, H);
        var req = new LiveStackRequest
        {
            Label = "cal", Center = new SkyTarget { RaHours = ra / 15, DecDegrees = dec, Epoch = "J2000" },
            FovWidthDegrees = W * 4 / 3600.0, FovHeightDegrees = H * 4 / 3600.0, PixelScaleArcsec = 4, Calibrate = calibrate,
        };
        req.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, req)).Ok.Value);

        // the sky: a smooth glow; the frame adds the vignetting and the dark signal
        static double Sky(int x, int y) => 3000 + 20.0 * y;
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) px[y * W + x] = (ushort)Math.Round(Sky(x, y) * Vignette(x, y) + Dark(x, y));
        var cards = new Dictionary<string, string> { ["EXPTIME"] = "0.2", ["CCD-TEMP"] = "-9.8", ["GAIN"] = "120" };
        foreach (var (k, v) in wcs.Cards()) cards[k] = v;
        await _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
        {
            Shooter = "cam", Format = ".fits", ExposureSeconds = 0.2, FrameType = "Light", Filter = "L", Timestamp = "t1", Data = new RawBytes(FitsImage.Write16(W, H, px, cards)),
        });
        Assert.True(await Eventually(() => last is { FramesStacked.Value: 1, FramesPending.Value: 0 }), last?.Message.Text);
        if (calibrate) Assert.Contains("dark+flat", last!.Message.Text);
        else Assert.DoesNotContain("flat", last!.Message.Text);

        var a = (await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Neutralize = false }, TimeSpan.FromSeconds(10)))!.Single();
        var img = FitsImage.Parse(a.Image.Data);
        // compare with the sky away from the edges: calibrated, it is the sky times a constant (the flat's median)
        var ratios = new List<double>();
        for (int y = 6; y < H - 6; y += 3) for (int x = 6; x < W - 6; x += 3)
            {
                float v = img.Data[y * img.Width + x];
                if (!float.IsNaN(v) && (x % 37 is < 3 or > 7)) ratios.Add(v / Sky(x, y));
            }
        double mean = ratios.Average(), spread = Math.Sqrt(ratios.Average(r => (r - mean) * (r - mean))) / mean;
        if (calibrate) Assert.True(spread < 0.01, $"calibrated frame still uneven: {spread:P2}");
        else Assert.True(spread > 0.05, $"uncalibrated frame unexpectedly even: {spread:P2}");
    }
}
