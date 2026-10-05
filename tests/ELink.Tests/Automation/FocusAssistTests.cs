using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The helper for focusing by hand follows what the person does to the focus.</summary>
public class FocusAssistTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FocusAssistState? _last;
    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("FA-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        await _node.HookEventAsync(FocusAssistIds.State, (FocusAssistState s) => _last = s);
    }
    public Task DisposeAsync() { _node.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static readonly (double X, double Y)[] Stars = [(40, 40), (120, 50), (200, 60), (60, 120), (140, 130), (200, 140), (50, 200), (120, 210), (190, 200)];

    /// <summary>A frame of stars of the given widths; for a colour frame (RGGB) a width for each colour.</summary>
    private static byte[] Frame(double sigmaR, double sigmaG, double sigmaB, bool colour)
    {
        const int W = 240, H = 240;
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double sigma = !colour ? sigmaG : (y % 2, x % 2) switch { (0, 0) => sigmaR, (1, 1) => sigmaB, _ => sigmaG };
                double v = 300;
                foreach (var (sx, sy) in Stars) v += 6000 * 4 / (sigma * sigma) * Math.Exp(-((x - sx) * (x - sx) + (y - sy) * (y - sy)) / (2 * sigma * sigma));
                px[y * W + x] = (ushort)Math.Min(60000, v);
            }
        return FitsImage.Write16(W, H, px, colour ? new Dictionary<string, string> { ["BAYERPAT"] = "'RGGB'" } : null);
    }

    [Fact]
    public async Task ItFollowsTheStarsAsTheyGetSharperAndSaysSo()
    {
        double sigma = 6;
        var shooter = new FakeShooter(_node, "cam") { FrameMaker = _ => Frame(sigma, sigma, sigma, false) };
        await shooter.StartAsync();
        await using var svc = new FocusAssistService(_node); await svc.StartAsync();
        Assert.False((await Commands.CallAsync(_node, FocusAssistIds.Start, new FocusAssistRequest())).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, FocusAssistIds.Start, new FocusAssistRequest { ShooterId = "cam", ExposureSeconds = 0.02 })).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Running" } && _last.Frames.Value >= 6), _last?.Message.Text);
        double soft = _last!.Samples[^1].Hfr.Value;
        Assert.Equal("steady", _last.Trend.Text);
        sigma = 3;     // somebody turned the knob
        Assert.True(await Eventually(() => _last is { Trend.Text: "sharper" }), _last?.Trend.Text);
        Assert.True(await Eventually(() => _last!.Samples[^1].Hfr.Value < soft * 0.7));
        Assert.True(_last!.BestHfr.Value <= _last.Samples[^1].Hfr.Value + 1e-9);
        sigma = 8;
        Assert.True(await Eventually(() => _last is { Trend.Text: "softer" }));
        Assert.True(_last!.BestHfr.Value < _last.Samples[^1].Hfr.Value);      // the best stays what it was

        Assert.True((await Commands.CallAsync(_node, FocusAssistIds.Reset, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last!.Frames.Value <= 2 || double.IsNaN(_last.BestHfr.Value) || _last.BestHfr.Value > 0));
        Assert.True((await Commands.CallAsync(_node, FocusAssistIds.Stop, NOTESVoid.Void)).Ok.Value);
        int n = shooter.Exposures; await Task.Delay(300);
        Assert.Equal(n, shooter.Exposures);                                   // it really stopped
        Assert.Equal("Idle", _last!.Phase.Text);
        await shooter.DisposeAsync();
    }

    [Fact]
    public async Task ColourFramesAreMeasuredColourByColour()
    {
        var shooter = new FakeShooter(_node, "cam") { FrameMaker = _ => Frame(4, 6, 9, true) };   // red sharpest, blue softest
        await shooter.StartAsync();
        await using var svc = new FocusAssistService(_node); await svc.StartAsync();
        Assert.True((await Commands.CallAsync(_node, FocusAssistIds.Start, new FocusAssistRequest { ShooterId = "cam", ExposureSeconds = 0.02 })).Ok.Value);
        Assert.True(await Eventually(() => _last is { Frames.Value: >= 1 } && !double.IsNaN(_last.Samples[^1].HfrBlue.Value)), _last?.Message.Text);
        var s = _last!.Samples[^1];
        Assert.True(s.HfrRed.Value < s.HfrGreen.Value && s.HfrGreen.Value < s.HfrBlue.Value, $"{s.HfrRed.Value} {s.HfrGreen.Value} {s.HfrBlue.Value}");
        Assert.InRange(s.Hfr.Value, s.HfrRed.Value, s.HfrBlue.Value);
        await Commands.CallAsync(_node, FocusAssistIds.Stop, NOTESVoid.Void);
        await shooter.DisposeAsync();
    }

    [Fact]
    public async Task ABrokenShooterIsReportedNotRetriedForever()
    {
        await using var svc = new FocusAssistService(_node); await svc.StartAsync();
        Assert.True((await Commands.CallAsync(_node, FocusAssistIds.Start, new FocusAssistRequest { ShooterId = "ghost", ExposureSeconds = 0.02 })).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Error" }, 15000), _last?.Phase.Text);
    }
}
