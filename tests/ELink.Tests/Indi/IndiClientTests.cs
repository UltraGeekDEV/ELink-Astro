using ELink.Indi.Client;
using ELink.Indi.Protocol;
using Xunit;

namespace ELink.Tests.Indi;

[Collection("indiserver")]
public class IndiClientTests(IndiServerFixture server)
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

    private async Task<IndiClient> Connect()
    {
        Assert.True(server.Available, "indiserver not available");
        var c = new IndiClient("127.0.0.1", server.Port);
        await c.ConnectAsync();
        return c;
    }

    [Fact]
    public async Task DiscoversAllSimulatorDevices()
    {
        await using var c = await Connect();
        await c.WaitForAsync("Telescope Simulator", "CONNECTION", _ => true, T);
        await c.WaitForAsync("CCD Simulator", "CONNECTION", _ => true, T);
        Assert.Contains("Telescope Simulator", c.Devices);
        Assert.Contains("CCD Simulator", c.Devices);
        Assert.Contains("Focuser Simulator", c.Devices);
    }

    [Fact]
    public async Task ConnectsAndSlewsTelescopeSimulator()
    {
        await using var c = await Connect();
        const string dev = "Telescope Simulator";
        await c.WaitForAsync(dev, "CONNECTION", _ => true, T);
        await c.SetSwitchAsync(dev, "CONNECTION", "CONNECT");
        await c.WaitForAsync(dev, "EQUATORIAL_EOD_COORD", _ => true, T);

        await c.SetSwitchAsync(dev, "ON_COORD_SET", "TRACK");
        await c.SetNumbersAsync(dev, "EQUATORIAL_EOD_COORD", new[] { ("RA", 5.5), ("DEC", 20.0) });
        var busy = await c.WaitForAsync(dev, "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Busy, T);
        Assert.Equal(IndiState.Busy, busy.State);
        var done = await c.WaitForAsync(dev, "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok && Math.Abs(p.Number("RA") - 5.5) < 0.01, TimeSpan.FromSeconds(60));
        Assert.Equal(20.0, done.Number("DEC"), 2);
    }

    [Fact]
    public async Task CapturesBlobFromCcdSimulator()
    {
        await using var c = await Connect();
        const string dev = "CCD Simulator";
        await c.WaitForAsync(dev, "CONNECTION", _ => true, T);
        await c.SetSwitchAsync(dev, "CONNECTION", "CONNECT");
        await c.WaitForAsync(dev, "CCD_EXPOSURE", _ => true, T);
        await c.EnableBlobAsync(dev);
        await c.SetNumberAsync(dev, "CCD_EXPOSURE", "CCD_EXPOSURE_VALUE", 1);
        var frame = await c.WaitForAsync(dev, "CCD1", p => p["CCD1"]?.Blob is { Length: > 0 }, TimeSpan.FromSeconds(30));
        var raw = IndiCodec.Decompress(frame["CCD1"]!.Blob!, frame["CCD1"]!.BlobFormat);
        Assert.True(raw.Length > 1000);
    }
}
