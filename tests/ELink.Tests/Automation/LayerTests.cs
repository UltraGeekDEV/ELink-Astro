using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Core.Astro;
using ELink.Contracts;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The coverage map with several layers, and how a frame is sorted into layers.</summary>
public class LayerTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    public Task InitializeAsync() { _node = ElinkNode.Create("LY-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next()); return Task.CompletedTask; }
    public Task DisposeAsync() { _node.Dispose(); return Task.CompletedTask; }

    private static Footprint[] Everywhere(CoverageMap m, int mask) => [new Footprint(0, 0, m.FovWidth, m.FovHeight, 0, mask)];

    [Fact]
    public void AShotAddsExposureOnlyToTheLayersItsFramesFeed()
    {
        var m = new CoverageMap(2, 2, 0.1, layers: 3);
        m.Paint(Everywhere(m, 0b101), 10);
        Assert.Equal(10, m.Min(0)); Assert.Equal(0, m.Max(1)); Assert.Equal(10, m.Min(2));
        m.Paint(Everywhere(m, 0b010), 4);
        Assert.Equal((10, 4, 10), (m.Mean(0), m.Mean(1), m.Mean(2)));
        // rolled back
        m.Paint(Everywhere(m, 0b101), -10);
        Assert.Equal(0, m.Max(0));
    }

    [Fact]
    public void DeficitAndGainLookAtOneLayer()
    {
        var m = new CoverageMap(1, 1, 0.1, layers: 2);
        m.Paint(Everywhere(m, 0b01), 100);
        m.DeficitAndWaste(Everywhere(m, 0b11), 100, out double d0, out int w0, layer: 0);
        m.DeficitAndWaste(Everywhere(m, 0b11), 100, out double d1, out int w1, layer: 1);
        Assert.Equal(0, d0); Assert.Equal(100, w0);        // layer 0 is done: a shot would only waste
        Assert.True(d1 > 0); Assert.Equal(0, w1);
        // a footprint that does not feed the layer says nothing about it
        m.DeficitAndWaste(Everywhere(m, 0b01), 100, out double d, out int w, layer: 1);
        Assert.Equal((0, 0), (d, w));
    }

    [Fact]
    public void LayersSurviveBeingKeptAndRestored()
    {
        var m = new CoverageMap(2, 1, 0.1, layers: 2);
        m.Paint(Everywhere(m, 0b01), 5); m.Paint([new Footprint(-0.5, 0, 0.2, 0.2, 0, 0b10)], 7);
        var back = CoverageMap.Restore(2, 1, m.Cols, m.Rows, m.Flatten(), 2);
        Assert.Equal(m.Planes[0], back.Planes[0]); Assert.Equal(m.Planes[1], back.Planes[1]);
        Assert.Throws<ArgumentException>(() => CoverageMap.Restore(2, 1, m.Cols, m.Rows, m.Planes[0], 2));
    }

    [Fact]
    public void TheProgressPictureShowsTheLeastAdvancedLayerAtEachSpot()
    {
        var m = new CoverageMap(1, 1, 0.1, layers: 2);
        m.Paint(Everywhere(m, 0b01), 100);   // layer 0 complete
        var bytes = m.RenderProgress([100, 200], 10, 10, out _, out _);
        Assert.All(bytes, b => Assert.Equal(0, b));          // layer 1 has nothing
        m.Paint(Everywhere(m, 0b10), 100);
        bytes = m.RenderProgress([100, 200], 10, 10, out _, out _);
        Assert.All(bytes, b => Assert.InRange(b, 126, 129)); // half way
    }

    [Fact]
    public void AFrameFeedsEveryLayerWhoseFilterAndScaleRangeItFits()
    {
        var layers = new[]
        {
            new ImagingLayer { Label = "base", Filter = "L", MaxScaleArcsec = 10 },
            new ImagingLayer { Label = "detail", Filter = "L", MaxScaleArcsec = 2 },
            new ImagingLayer { Label = "ha", Filter = "Ha", MinScaleArcsec = 1 },
        };
        string[] Of(string filter, double scale) => layers.Where(l => LiveStackService.LayerFor([l], filter, scale) is not null).Select(l => l.Label.Text).ToArray();
        Assert.Equal(["base", "detail"], Of("L", 1.5));     // fine data goes to both
        Assert.Equal(["base"], Of("l", 6));                 // filters are not case sensitive
        Assert.Empty(Of("L", 12));
        Assert.Equal(["ha"], Of("Ha", 3));
        Assert.Empty(Of("Ha", 0.5));
        Assert.Equal(["base", "detail"], Of("L", 2));       // the ends of a range are in it
    }

    [Fact]
    public async Task TheLiveStackKeepsOneStackPerLayerAndGivesAFrameToEveryLayerItFits()
    {
        await using var svc = new LiveStackService(_node); await svc.StartAsync();
        var request = new LiveStackRequest
        {
            Label = "layers", Center = new SkyTarget { RaHours = 5.6, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = 0.05, FovHeightDegrees = 0.05, PixelScaleArcsec = 3, Interpolation = "Nearest",
        };
        request.ShooterIds.Add("cam");
        request.Layers.Add(new ImagingLayer { Label = "base", Filter = "L", MaxScaleArcsec = 10 });
        request.Layers.Add(new ImagingLayer { Label = "detail", Filter = "L", MaxScaleArcsec = 2 });
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);
        int n = 0;
        Task Shoot(double scale, string filter)
        {
            var wcs = TanWcs.Centered(84, 10, 0, scale, 60, 60);
            var cards = new Dictionary<string, string> { ["EXPTIME"] = "30" };
            foreach (var (k, v) in wcs.Cards()) cards[k] = v;
            return _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
            {
                Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Filter = filter, Timestamp = "t" + ++n,
                Data = new RawBytes(FitsImage.Write16(60, 60, Enumerable.Range(0, 3600).Select(i => (ushort)(1000 + i % 7 + n)).ToArray(), cards)),
            });
        }
        async Task<bool> Eventually(Func<bool> c) { for (int i = 0; i < 500 && !c(); i++) await Task.Delay(20); return c(); }
        await Shoot(6, "L");      // base only
        await Shoot(1.5, "L");    // both
        await Shoot(1.5, "Ha");   // no layer takes Ha
        await Shoot(30, "L");     // too coarse for any
        Assert.True(await Eventually(() => state is not null && state.FramesPending.Value == 0 && state.Filters.Count >= 2 && state.FramesStacked.Value >= 3), state?.Message.Text);
        var filters = state!.Filters.Select(f => f.Text).ToList();
        Assert.Contains("base: 2", filters); Assert.Contains("detail: 1", filters);
        var img = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Filter = "detail" }, TimeSpan.FromSeconds(10)))!);
        Assert.True(img.Ok.Value, img.Message.Text);
        Assert.Equal("L", FitsImage.Parse(img.Image.Data).Get("FILTER")?.Trim('\'', ' '));
        Assert.Equal("detail", FitsImage.Parse(img.Image.Data).Get("LAYER")?.Trim('\'', ' '));
    }

    [Fact]
    public async Task TheCombinedImageLaysTheFinerLayersDetailOverTheCoarseBase()
    {
        await using var svc = new LiveStackService(_node); await svc.StartAsync();
        var request = new LiveStackRequest
        {
            Label = "combo", Center = new SkyTarget { RaHours = 84 / 15.0, DecDegrees = 10, Epoch = "J2000" }, FovWidthDegrees = 180 / 3600.0, FovHeightDegrees = 180 / 3600.0, PixelScaleArcsec = 1.5, Interpolation = "Nearest",
        };
        request.ShooterIds.Add("cam");
        request.Layers.Add(new ImagingLayer { Label = "base", Filter = "L", MinScaleArcsec = 4 });
        request.Layers.Add(new ImagingLayer { Label = "detail", Filter = "L", MaxScaleArcsec = 2 });
        Assert.True((await Commands.CallAsync(_node, LiveStackIds.Start, request)).Ok.Value);
        LiveStackState? state = null;
        await _node.HookEventAsync(LiveStackIds.State, (LiveStackState s) => state = s);
        int n = 0;
        // one star: a blur of 12" at 6"/px (its light spread out), sharp at 1.5"/px (the same light in a 2.25" star)
        Task Shoot(double scale, int size, double sigmaArcsec, double amplitude)
        {
            var wcs = TanWcs.Centered(84, 10, 0, scale, size, size);
            var px = new ushort[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = (x + 0.5 - size / 2.0) * scale, dy = (y + 0.5 - size / 2.0) * scale;
                    px[y * size + x] = (ushort)Math.Round(1000 + amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * sigmaArcsec * sigmaArcsec)));
                }
            var cards = new Dictionary<string, string> { ["EXPTIME"] = "30" };
            foreach (var (k, v) in wcs.Cards()) cards[k] = v;
            return _node.FireEventAsync(ShooterIds.Shot("cam"), new ShotEvent
            {
                Shooter = "cam", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Filter = "L", Timestamp = "t" + ++n, Data = new RawBytes(FitsImage.Write16(size, size, px, cards)),
            });
        }
        async Task<bool> Eventually(Func<bool> c) { for (int i = 0; i < 500 && !c(); i++) await Task.Delay(20); return c(); }
        async Task<(float[] Data, FitsImage Img)> Get(string filter)
        {
            var img = Assert.Single((await _node.CallFunctionAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, new LiveStackImageRequest { Filter = filter, Neutralize = false }, TimeSpan.FromSeconds(10)))!);
            Assert.True(img.Ok.Value, img.Message.Text);
            var f = FitsImage.Parse(img.Image.Data);
            return (f.Data, f);
        }
        await Shoot(6, 30, 12, 500);
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 1), state?.Message.Text);
        // with only the base so far, the combined image is the base
        var (early, _) = await Get("");
        var (baseOnly, _) = await Get("base");
        Assert.Equal(baseOnly, early);
        await Shoot(1.5, 120, 2.25, 14000);
        Assert.True(await Eventually(() => state is not null && state.FramesStacked.Value == 2), state?.Message.Text);

        var (combined, img) = await Get("");
        int w = img.Width, centre = (w / 2) * w + w / 2, away = (w / 2) * w + 5;
        Assert.Equal("base+detail", img.Get("LAYERS")?.Trim('\'', ' ').Replace(" ", ""));
        float peakBase = baseOnly[centre] - 1000, peakCombined = combined[centre] - 1000;
        Assert.True(peakCombined > 4 * peakBase, $"base {peakBase}, combined {peakCombined}");        // the star is sharp again
        Assert.True(Math.Abs(combined[away] - baseOnly[away]) < 30, $"{combined[away]} vs {baseOnly[away]}");   // away from it, the base is what there is
        // the same light: the star's total in the combined image is what the base has
        double Total(float[] d) { double t = 0; for (int i = 0; i < w * w; i++) t += d[i] - 1000; return t; }
        Assert.InRange(Total(combined) / Total(baseOnly), 0.8, 1.25);
        var (detailOnly, _) = await Get("detail");
        Assert.NotEqual(baseOnly[centre], detailOnly[centre]);
    }

    [Fact]
    public void ABlurKeepsLightAndLeavesGapsAlone()
    {
        var plane = new float[21 * 21];
        plane[10 * 21 + 10] = 100;
        plane[0] = float.NaN;
        var soft = LiveStackService.Blur(plane, 21, 21, 2);
        Assert.True(float.IsNaN(soft[0]));
        Assert.True(soft[10 * 21 + 10] < 20 && soft[10 * 21 + 10] > soft[10 * 21 + 14]);
        Assert.InRange(soft.Where(v => !float.IsNaN(v)).Sum(), 90, 110);
        Assert.Equal(plane, LiveStackService.Blur(plane, 21, 21, 0.1));
    }
}
