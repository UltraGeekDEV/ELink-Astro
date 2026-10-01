using System.Net;
using Event.CoreFunctionality;
using Event.CoreFunctionality.EVentIDs;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.TCP;

namespace ELink.Core;

/// <summary>Creating and joining EVent nodes. Every ELink process (bridge, UI, sequencer, ...) is one node of
/// the mesh; they know each other only as "something that runs EVent".</summary>
public static class ElinkNode
{
    public const int DefaultPort = 5698;

    /// <summary>A node listening on TCP and discoverable by its own name through multicast.</summary>
    /// <remarks>The node's constructor blocks until its asynchronous start-up is done. If that start-up resumes on the caller's
    /// synchronization context (a UI dispatcher, a test framework's limited scheduler) and every thread of that context is busy
    /// waiting, nothing can make progress until the start-up times out. So the node is always constructed on a pool thread with
    /// no synchronization context; the caller still gets it synchronously.</remarks>
    public static TypeSafeEVentNode Create(string name, int port, IPAddress? listen = null) =>
        SynchronizationContext.Current is null && !Thread.CurrentThread.IsThreadPoolThread
            ? Construct(name, port, listen)
            : Task.Run(() => Construct(name, port, listen)).GetAwaiter().GetResult();

    /// <summary>Same as <see cref="Create"/>, without blocking the caller.</summary>
    public static Task<TypeSafeEVentNode> CreateAsync(string name, int port, IPAddress? listen = null) =>
        Task.Run(() => Construct(name, port, listen));

    private static TypeSafeEVentNode Construct(string name, int port, IPAddress? listen) =>
        new(name, new TCPServer(listen ?? IPAddress.Loopback, port, name));

    public static async Task<MergeResult> JoinAsync(TypeSafeEVentNode node, string host, int port)
    {
        var ip = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return await node.TryEstablishConnectionAsync(new Package(ConnectionTypes.TCP, new TCPConnectionData(new IPEndPoint(ip, port))));
    }

    public static Task<MergeResult> JoinByDiscoveryAsync(TypeSafeEVentNode node, string peerName) =>
        node.TryEstablishConnectionAsync(new Package(ConnectionTypes.TCPDiscovery, peerName));
}
