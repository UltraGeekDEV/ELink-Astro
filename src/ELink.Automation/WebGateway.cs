using System.Net;
using EVent.Connections;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.WebSocket;
using EVent.Endpoints;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>The web interface, served by the station's own node: a file endpoint holds the page, a web endpoint serves it over HTTP,
/// and the page joins the mesh as a JavaScript leaf over the node's WebSocket server. The page talks only EVent, exactly like the
/// desktop window; nothing about the backend is known to it but IDs and contracts.
/// <para>There is no login: by default everything listens on loopback. Opening it to the network (<c>--web-listen 0.0.0.0</c>) lets
/// any page on any machine that can reach the ports control the telescope, so do that only on a network you trust.</para></summary>
public sealed class WebGateway : IDisposable
{
    public const string WsPortId = "ELink.Web.WebSocketPort";
    private readonly FileEndpoint _site;
    private readonly WebEndpoint _web;

    /// <summary>The WebSocket transport for the station's node (give it to the node when it is made): browser leaves dial it.</summary>
    public static ICommsProtocol Transport(IPAddress listen, int wsPort) =>
        IPAddress.IsLoopback(listen)
            ? new WebSocketServer($"ws://{(listen.Equals(IPAddress.IPv6Loopback) ? "[::1]" : "127.0.0.1")}:{wsPort}/")
            : new WebSocketServer($"ws://{(listen.Equals(IPAddress.Any) ? "+" : listen.ToString())}:{wsPort}/", "*");

    public WebGateway(TypeSafeEVentNode node, string siteFolder, IPAddress listen, int httpPort, int wsPort)
    {
        _site = new FileEndpoint(node, siteFolder, allowSave: false, name: "ELinkWebSite");
        string host = IPAddress.IsLoopback(listen) ? (listen.Equals(IPAddress.IPv6Loopback) ? "[::1]" : "127.0.0.1") : listen.Equals(IPAddress.Any) ? "+" : listen.ToString();
        _web = new WebEndpoint(node, $"http://{host}:{httpPort}/");
        if (!IPAddress.IsLoopback(listen)) _web.AllowedOrigins = ["*"];
        if (!node.RegisterFunction<NOTESVoid, BinaryConvertibleInt32>(WsPortId, _ => wsPort, "the WebSocket port of this station's web interface"))
            throw new InvalidOperationException($"{WsPortId} is already used with another type");
        _web.Get("/api/wsport", WsPortId);
        const string indexId = "ELink.Web.Index";
        _site.Route(indexId, "index.html");
        _web.ServeFile("/", indexId);
        _web.ServeFiles("/{*path}", _site.GetFileEventID);     // last: the catch-all
        _web.Start();
        Port = httpPort;
    }

    public int Port { get; }

    public void Dispose() { _web.Dispose(); _site.Dispose(); }

    /// <summary>The page next to the executable (or in the source tree when running from it).</summary>
    public static string? FindSite(params string[] hints)
    {
        foreach (var h in hints) if (Directory.Exists(h)) return h;
        var next = Path.Combine(AppContext.BaseDirectory, "web");
        return Directory.Exists(next) ? next : null;
    }
}
