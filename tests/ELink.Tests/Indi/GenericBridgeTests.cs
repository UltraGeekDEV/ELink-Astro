using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ELink.Contracts.Indi;
using ELink.Core;
using ELink.Indi.Client;
using ELink.IndiBridge;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Indi;

[Collection("indiserver")]
public class GenericBridgeTests(IndiServerFixture server)
{
    private static int FreePort() => ELink.Testing.TestPorts.Next();

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    [Fact]
    public async Task ConsumerSeesAndControlsSimulatorThroughEVentOnly()
    {
        Assert.True(server.Available);
        const string srv = "Sim";
        int bridgePort = FreePort();
        using var bridgeNode = ElinkNode.Create("T-Bridge", bridgePort);
        await using var publisher = new IndiGenericPublisher(bridgeNode, srv);
        await publisher.StartAsync();

        // consumer: a separate node that knows only EVent IDs and contract types
        using var consumer = ElinkNode.Create("T-Consumer", FreePort());
        var props = new ConcurrentDictionary<string, IndiPropertyInfo>();
        await consumer.HookEventAsync(IndiIds.Property(srv), (IndiPropertyEvent e) =>
        {
            string key = e.Info.Device.Text + "/" + e.Info.Property.Text;
            if (e.Kind.Text == "Deleted") props.TryRemove(key, out _); else props[key] = e.Info;
        });
        Assert.Equal(MergeResult.Connected, await ElinkNode.JoinAsync(consumer, "127.0.0.1", bridgePort));

        await using var client = new IndiClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        await publisher.Attach(client);

        Assert.True(await Eventually(() => props.ContainsKey("Telescope Simulator/CONNECTION")));

        async Task<IndiResult> Set(string device, string prop, params (string, string)[] values)
        {
            var req = new IndiSetRequest { Device = device, Property = prop };
            foreach (var (id, v) in values) req.Elements.Add(new IndiSetElement { Id = id, Value = v });
            var answers = await consumer.CallFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set(srv), req);
            return Assert.Single(answers!);
        }

        Assert.True((await Set("Telescope Simulator", "CONNECTION", ("CONNECT", "On"))).Ok.Value);
        Assert.True(await Eventually(() => props.ContainsKey("Telescope Simulator/EQUATORIAL_EOD_COORD")));
        Assert.True((await Set("Telescope Simulator", "ON_COORD_SET", ("TRACK", "On"))).Ok.Value);
        Assert.True((await Set("Telescope Simulator", "EQUATORIAL_EOD_COORD", ("RA", "6:00:00"), ("DEC", "-10"))).Ok.Value);

        Assert.True(await Eventually(() =>
            props.TryGetValue("Telescope Simulator/EQUATORIAL_EOD_COORD", out var p) && p.State.Text == "Ok"
            && Math.Abs(double.Parse(p.Elements.First(x => x.Id.Text == "RA").Value.Text, System.Globalization.CultureInfo.InvariantCulture) - 6.0) < 0.01, 60000));

        // errors come back as results, not exceptions
        Assert.False((await Set("Telescope Simulator", "NO_SUCH_PROPERTY", ("X", "1"))).Ok.Value);
        Assert.False((await Set("Telescope Simulator", "EQUATORIAL_EOD_COORD", ("RA", "abc"))).Ok.Value);

        // a late joiner can catch up through the snapshot
        var snap = Assert.Single((await consumer.CallFunctionAsync<NOTESVoid, IndiSnapshot>(IndiIds.Snapshot(srv), NOTESVoid.Void))!);
        Assert.True(snap.Connected.Value);
        Assert.True(snap.Properties.Count > 20);
    }

    [Fact]
    public async Task FramesArriveAsBlobEvents()
    {
        Assert.True(server.Available);
        const string srv = "SimCcd";
        int bridgePort = FreePort();
        using var bridgeNode = ElinkNode.Create("T-Bridge2", bridgePort);
        await using var publisher = new IndiGenericPublisher(bridgeNode, srv);
        await publisher.StartAsync();
        using var consumer = ElinkNode.Create("T-Consumer2", FreePort());
        IndiBlobEvent? blob = null;
        await consumer.HookEventAsync(IndiIds.Blob(srv), (IndiBlobEvent b) => blob = b);
        await consumer.HookEventAsync(IndiIds.Property(srv), (IndiPropertyEvent _) => { });
        await ElinkNode.JoinAsync(consumer, "127.0.0.1", bridgePort);

        await using var client = new IndiClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        await publisher.Attach(client);

        async Task Set(string prop, string id, string value)
        {
            var req = new IndiSetRequest { Device = "CCD Simulator", Property = prop };
            req.Elements.Add(new IndiSetElement { Id = id, Value = value });
            Assert.True(Assert.Single((await consumer.CallFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set(srv), req))!).Ok.Value);
        }
        Assert.True(await Eventually(() => client.GetProperty("CCD Simulator", "CONNECTION") is not null));
        await Set("CONNECTION", "CONNECT", "On");
        Assert.True(await Eventually(() => client.GetProperty("CCD Simulator", "CCD_EXPOSURE") is not null));
        var enable = await consumer.CallFunctionAsync<IndiBlobRequest, IndiResult>(IndiIds.EnableBlob(srv), new IndiBlobRequest { Device = "CCD Simulator" });
        Assert.True(Assert.Single(enable!).Ok.Value);
        await Set("CCD_EXPOSURE", "CCD_EXPOSURE_VALUE", "1");
        Assert.True(await Eventually(() => blob is not null, 30000));
        Assert.True(blob!.Data.Data.Length > 1000);
        Assert.Equal("CCD1", blob.Property.Text);
    }
}
