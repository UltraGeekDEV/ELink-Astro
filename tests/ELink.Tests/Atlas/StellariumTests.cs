using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ELink.Contracts.Atlas;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Stellarium;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Atlas;

public class TelescopeProtocolTests
{
    [Fact]
    public void EncodesTheWayStellariumDoes()
    {
        Assert.Equal(0x80000000u, TelescopeProtocol.EncodeRa(12));
        Assert.Equal(0u, TelescopeProtocol.EncodeRa(24));                       // wraps
        Assert.Equal(0x40000000, TelescopeProtocol.EncodeDec(90));
        Assert.Equal(-0x40000000, TelescopeProtocol.EncodeDec(-90));
        Assert.Equal(5.5, TelescopeProtocol.DecodeRa(TelescopeProtocol.EncodeRa(5.5)), 6);
        Assert.Equal(-33.25, TelescopeProtocol.DecodeDec(TelescopeProtocol.EncodeDec(-33.25)), 6);
    }

    [Fact]
    public void ParsesGotoMessagesAndWaitsForWholeOnes()
    {
        var msg = TelescopeProtocol.Goto(18.6, 38.8);
        Assert.Equal(20, msg.Length);
        Assert.Equal(0, TelescopeProtocol.TryParse(msg.AsSpan(0, 10), out _, out _, out _));        // incomplete
        Assert.Equal(20, TelescopeProtocol.TryParse(msg, out ushort type, out double ra, out double dec));
        Assert.Equal(0, type); Assert.Equal(18.6, ra, 6); Assert.Equal(38.8, dec, 6);
        var pos = TelescopeProtocol.Position(1, 2, 0);
        Assert.Equal(24, pos.Length);
        Assert.Equal(24, BitConverter.ToUInt16(pos, 0));
    }
}

public class StellariumTelescopeServerTests : IAsyncLifetime
{
    private static int FreePort() => ELink.Testing.TestPorts.Next();
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("ST-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        _pointer = new FakePointer(_node, "sp", 20); await _pointer.StartAsync();
    }

    public async Task DisposeAsync() { await _pointer.DisposeAsync(); _node.Dispose(); }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task StellariumSeesWherethePointerIsAndCanSlewIt()
    {
        await using var server = new StellariumTelescopeServer(_node, 0) { Heartbeat = TimeSpan.FromMilliseconds(100) };
        await server.StartAsync("sp");
        using var stellarium = new TcpClient();                                      // plays the Telescope Control plugin
        await stellarium.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = stellarium.GetStream();
        Assert.True(await Eventually(() => server.ClientCount == 1));

        // Stellarium: "slew to Vega" (J2000)
        await stream.WriteAsync(TelescopeProtocol.Goto(18.6156, 38.7837));
        Assert.True(await Eventually(() => _pointer.Gotos.Count == 1));
        Assert.Equal(18.6156, _pointer.Gotos[0].RaHours.Value, 3);
        Assert.Equal("J2000", _pointer.Gotos[0].Epoch.Text);

        // ...and the position it gets back follows the pointer
        var buf = new byte[24];
        double ra = 0, dec = 0;
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && Math.Abs(ra - 18.6156) > 1e-3)
        {
            int read = 0;
            while (read < 24) read += await stream.ReadAsync(buf.AsMemory(read));
            Assert.Equal(24, BitConverter.ToUInt16(buf, 0));
            ra = TelescopeProtocol.DecodeRa(BitConverter.ToUInt32(buf, 12)); dec = TelescopeProtocol.DecodeDec(BitConverter.ToInt32(buf, 16));
        }
        Assert.Equal(18.6156, ra, 3); Assert.Equal(38.7837, dec, 3);
        Assert.Equal(0, BitConverter.ToInt32(buf, 20));                               // status ok
    }

    [Fact]
    public async Task ThePointerCanBeRebound()
    {
        await using var other = new FakePointer(_node, "other", 20); await other.StartAsync();
        await using var server = new StellariumTelescopeServer(_node, 0);
        await server.StartAsync("sp");
        await server.BindAsync("other");
        using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, server.Port);
        await c.GetStream().WriteAsync(TelescopeProtocol.Goto(1, 2));
        Assert.True(await Eventually(() => other.Gotos.Count == 1));
        Assert.Empty(_pointer.Gotos);
    }
}

/// <summary>A pretend Stellarium Remote Control: answers like the plugin does.</summary>
internal sealed class FakeRemoteControl : HttpMessageHandler
{
    public string? Selection { get; set; } = """{"name":"Vega","localized-name":"Vega","object-type":"star","raJ2000":279.2347,"decJ2000":38.7837,"vmag":0.03}""";
    public List<(string Path, string Body)> Posts { get; } = new();
    public bool Down { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("connection refused");
        string path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            Posts.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
        return path switch
        {
            "/api/main/status" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"selectioninfo":"","view":{"fov":60}}""") },
            "/api/objects/info" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Selection ?? "no current selection, and no name parameter given") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }
}

public class StellariumRemoteTests
{
    private static int FreePort() => ELink.Testing.TestPorts.Next();

    [Fact]
    public async Task ShowsAPositionAsAJ2000DirectionAndReadsTheSelection()
    {
        var fake = new FakeRemoteControl();
        using var remote = new StellariumRemote("http://127.0.0.1:8090", fake);
        Assert.True(await remote.PingAsync());
        await remote.ShowAsync(6.0, 0.0);                                             // RA 90 degrees on the equator: the +y axis
        var (path, body) = fake.Posts.Single();
        Assert.Equal("/api/main/focus", path);
        var vec = Uri.UnescapeDataString(body.Replace('+', ' ')).Split('=')[1].Trim('[', ']').Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(0, vec[0], 6); Assert.Equal(1, vec[1], 6); Assert.Equal(0, vec[2], 6);

        var sel = await remote.GetSelectionAsync();
        Assert.NotNull(sel);
        Assert.Equal("Vega", sel!.Name); Assert.Equal(18.6156, sel.RaHours, 3); Assert.Equal(38.7837, sel.DecDegrees, 3);
        fake.Selection = null;
        Assert.Null(await remote.GetSelectionAsync());
    }

    [Fact]
    public async Task TheServiceAnnouncesSelectionChangesAndSaysWhenStellariumIsAway()
    {
        var fake = new FakeRemoteControl();
        using var node = ElinkNode.Create("SR-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        await using var svc = new StellariumService(node, 0, "http://127.0.0.1:8090", fake) { PollInterval = TimeSpan.FromMilliseconds(100) };
        var selected = new List<AtlasHit>();
        await node.HookEventAsync(StellariumIds.Selected, (AtlasHit h) => { lock (selected) selected.Add(h); });
        await svc.StartAsync();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until) { lock (selected) if (selected.Count > 0) break; await Task.Delay(20); }
        lock (selected) Assert.Equal("Vega", selected.Single().Label.Text);         // announced once, not on every poll

        var shown = await Commands.CallAsync(node, StellariumIds.Show, new SkyTarget { RaHours = 5.5, DecDegrees = -5, Epoch = "J2000" });
        Assert.True(shown.Ok.Value);
        fake.Down = true;
        var refused = await Commands.CallAsync(node, StellariumIds.Show, new SkyTarget { RaHours = 5.5, DecDegrees = -5, Epoch = "J2000" });
        Assert.False(refused.Ok.Value);
        Assert.Contains("not reachable", refused.Error.Text);
    }
}
