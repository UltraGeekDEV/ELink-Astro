using System.Net;
using ELink.Automation;
using ELink.Core;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The web interface: the station's node serves the page over HTTP from a file endpoint and tells the page which WebSocket
/// port to join the mesh on.</summary>
public class WebGatewayTests
{
    [Fact]
    public async Task TheNodeServesThePageAndSaysWhereToJoin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elink-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>hello</h1>");
        File.WriteAllText(Path.Combine(dir, "app.js"), "export const x = 1;");
        int http = ELink.Testing.TestPorts.Next(), ws = ELink.Testing.TestPorts.Next();
        var node = ElinkNode.Create("WEB-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next(), null, WebGateway.Transport(IPAddress.Loopback, ws));
        try
        {
            using var gateway = new WebGateway(node, dir, IPAddress.Loopback, http, ws);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http}/") };
            Assert.Equal("<h1>hello</h1>", await client.GetStringAsync("/"));
            Assert.Equal("export const x = 1;", await client.GetStringAsync("/app.js"));
            Assert.Equal(ws.ToString(), (await client.GetStringAsync("/api/wsport")).Trim());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/nothing.js")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/../secret")).StatusCode);
        }
        finally { node.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }
}
