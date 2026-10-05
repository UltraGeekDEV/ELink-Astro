using ELink.Automation;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Imaging;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>Pseudo mono: one channel of each colour frame in focus. Those go to the colour stack, the other two (soft) to a luminance
/// stack; the image takes as much of that luminance as is asked for; storage keeps the two apart in two folders.</summary>
public class PseudoMonoTests : IAsyncLifetime
{
    private const int W = 120, H = 120;
    private const double Scale = 2;
    private static readonly double[] Amplitude = [3000, 2000, 1000];   // a reddish star
    private TypeSafeEVentNode _node = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elink-pm-" + Guid.NewGuid().ToString("N"));
    private int _shots;

    public Task InitializeAsync() { _node = ElinkNode.Create("PM-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next()); return Task.CompletedTask; }
    public Task DisposeAsync() { _node.Dispose(); try { Directory.Delete(_dir, true); } catch { } return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    /// <summary>An RGGB frame of one star: the channel in focus is sharp, the others soft (the same light, spread out).</summary>
    private static byte[] Frame(int inFocus, TanWcs wcs)
    {
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c = (y % 2, x % 2) switch { (0, 0) => 0, (1, 1) => 2, _ => 1 };
                double sigma = c == inFocus ? 2 : 6;
                double r2 = (x - W / 2.0) * (x - W / 2.0) + (y - H / 2.0) * (y - H / 2.0);
                double flux = Amplitude[c] * 2 * 2 / (sigma * sigma);   // equal total light in focus or not
                px[y * W + x] = (ushort)Math.Round(500 + flux * Math.Exp(-r2 / (2 * sigma * sigma)));
            }
        var cards = new Dictionary<string, string> { ["EXPTIME"] = "30", ["BAYERPAT"] = "'RGGB'" };
        foreach (var (k, v) in wcs.Cards()) cards[k] = v;
        return FitsImage.Write16(W, H, px, cards);
    }

    private static TanWcs Wcs => TanWcs.Centered(83.8, -5.4, 0, Scale, W, H);

    private Task Shoot(string channel) =>
        _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
        {
            Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Timestamp = $"t{Interlocked.Increment(ref _shots)}",
            PseudoChannel = channel, PointingRaHours = 83.8 / 15, PointingDecDegrees = -5.4, Data = new RawBytes(Frame("RGB".IndexOf(channel), Wcs)),
        });

    private async Task<float[]> Composite(double weight)
    {
        var answers = await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage,
            new LiveStackImageRequest { Neutralize = false, OutOfFocusWeight = weight }, TimeSpan.FromSeconds(10));
        var a = Assert.Single(answers!);
        Assert.True(a.Ok.Value, a.Message.Text);
        Assert.Equal(("pseudo mono", 3, 3), (a.Filter.Text, a.Channels.Value, a.Frames.Value));
        return FitsImage.Parse(a.Image.Data).Data;
    }

    [Fact]
    public async Task InFocusChannelsMakeTheColourAndTheSoftLightIsMixedInOnlyWhenAsked()
    {
        await using var svc = new LiveStackService(_node, _dir); await svc.StartAsync();
        var request = new LiveStackRequest
        {
            Label = "pm", Center = new SkyTarget { RaHours = 83.8 / 15, DecDegrees = -5.4, Epoch = "J2000" }, FovWidthDegrees = W * Scale / 3600, FovHeightDegrees = H * Scale / 3600,
            PixelScaleArcsec = Scale, Interpolation = "Nearest",
        };
        request.ShooterIds.Add("cam");
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);

        // nothing to show until each colour has been in focus once
        await Shoot("R"); await Shoot("G");
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 2));
        var early = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest(), TimeSpan.FromSeconds(10)))!);
        Assert.False(early.Ok.Value);
        Assert.Contains("B: 0", early.Message.Text);
        await Shoot("B");
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 3), state?.Message.Text);
        Assert.Equal(3, state!.Channels.Value);
        Assert.Contains(state.Filters, f => f.Text.StartsWith("pm:L"));

        const int plane = W * H;
        float At(float[] d, int c, int dx) => d[c * plane + (H / 2) * W + W / 2 + dx];
        var sharp = await Composite(0);
        var mixed = await Composite(1);
        // out of the 3 frames each colour was sharp in one: its star is as tall as the sharp star is, and has no halo
        for (int c = 0; c < 3; c++)
        {
            Assert.True(At(sharp, c, 0) > 0.8 * (500 + Amplitude[c]), $"channel {c}: {At(sharp, c, 0)}");
            Assert.True(At(sharp, c, 8) < 500 + 0.02 * Amplitude[c], $"channel {c} at 8px: {At(sharp, c, 8)}");
        }
        // with the soft light the star's wings are there, and the same in all colours (a white halo, no colour fringe)
        float r8 = At(mixed, 0, 8), g8 = At(mixed, 1, 8), b8 = At(mixed, 2, 8);
        Assert.True(g8 > At(sharp, 1, 8) + 20, $"{g8} vs {At(sharp, 1, 8)}");
        Assert.True(Math.Abs(r8 - b8) < 0.15 * (g8 - 500) + 5, $"r {r8} b {b8} g {g8}");
        // and a weight in between is in between
        var half = await Composite(0.5);
        Assert.InRange(At(half, 1, 8), At(sharp, 1, 8), g8);
    }

    [Fact]
    public async Task AFrameWithoutAColourFilterArrayCannotBeSplitAndSaysSo()
    {
        await using var svc = new LiveStackService(_node, _dir); await svc.StartAsync();
        var request = new LiveStackRequest { Label = "pm", Center = new SkyTarget { RaHours = 83.8 / 15, DecDegrees = -5.4, Epoch = "J2000" }, FovWidthDegrees = 0.05, FovHeightDegrees = 0.05, PixelScaleArcsec = Scale };
        request.ShooterIds.Add("cam");
        await Commands.CallAsync(_node, LiveStackIds.Start, request);
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);
        var cards = new Dictionary<string, string>(); foreach (var (k, v) in Wcs.Cards()) cards[k] = v;
        await _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
        {
            Shooter = "cam", Format = ".fits", ExposureSeconds = 1, FrameType = "Light", Timestamp = "x", PseudoChannel = "G", Data = new RawBytes(FitsImage.Write16(W, H, new ushort[W * H], cards)),
        });
        Assert.True(await Eventually(() => state is not null && state.FramesRejected.Value == 1));
        Assert.Contains("debayered", state!.Message.Text);
    }

    [Fact]
    public async Task StorageKeepsTheInFocusChannelAndTheOutOfFocusLightInTwoFolders()
    {
        await using var storage = new StorageService(_node, _dir) { LocalNow = () => new DateTime(2026, 10, 2, 1, 30, 0) };
        await storage.StartAsync();
        Assert.True((await Commands.CallAsync(_node, StorageIds.Watch, new StorageWatch { ShooterId = "cam", Enabled = true })).Ok.Value);
        await Shoot("R"); await Shoot("B");
        Assert.True(await Eventually(() => storage.FramesSaved == 2));
        string night = Path.Combine(_dir, "2026-10-01");
        var sharp = Directory.GetFiles(Path.Combine(night, "infocus"), "*.fits").Order().ToArray();
        var soft = Directory.GetFiles(Path.Combine(night, "oof"), "*.fits").Order().ToArray();
        Assert.Equal(2, sharp.Length); Assert.Equal(2, soft.Length);
        var red = sharp.Single(f => f.Contains("_R_")); Assert.Single(sharp, f => f.Contains("_B_"));
        var r = FitsImage.Parse(File.ReadAllBytes(red)); var o = FitsImage.Parse(File.ReadAllBytes(soft.Single(f => f.Contains("_R_"))));
        Assert.Equal((W / 2, H / 2), (r.Width, r.Height));   // super pixel: nothing interpolated across channels
        Assert.Equal("R", r.Get("PSEUDOCH")?.Trim('\''));
        float peak(FitsImage i) => i.Data.Max();
        Assert.True(peak(r) > peak(o) * 1.5, $"{peak(r)} vs {peak(o)}");   // the sharp channel's star is the taller
        Assert.Empty(Directory.GetFiles(night, "*.fits"));                  // nothing loose beside the two folders
    }
}
