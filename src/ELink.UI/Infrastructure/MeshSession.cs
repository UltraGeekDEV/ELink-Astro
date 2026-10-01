using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using ELink.Core;
using Event.CoreFunctionality;

namespace ELink.UI.Infrastructure;

/// <summary>The UI's single point of contact with the world: one EVent node. It either owns a fresh node and joins a
/// mesh, or is handed the node of the process it runs in (all-in-one station: local loopback, no sockets involved).
/// Nothing else in the UI may know how the backend is reached.</summary>
public sealed partial class MeshSession : ObservableObject, IDisposable
{
    private readonly bool _ownsNode;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _status = "not connected";

    public TypeSafeEVentNode Node { get; }

    private MeshSession(TypeSafeEVentNode node, bool ownsNode)
    {
        Node = node; _ownsNode = ownsNode;
        if (!ownsNode) { IsConnected = true; Status = "running in-process"; }
    }

    /// <summary>Creates the session. A node's constructor blocks until it is ready, which deadlocks on a UI thread's
    /// synchronization context, so a private node is always created on a pool thread.</summary>
    /// <param name="node">the hosting process's node, or null to create a private one</param>
    public static async Task<MeshSession> CreateAsync(TypeSafeEVentNode? node = null, string name = "ELink-UI")
    {
        if (node is not null) return new MeshSession(node, ownsNode: false);
        var created = await Task.Run(() => ElinkNode.Create(name + "-" + Environment.ProcessId, FreePort()));
        return new MeshSession(created, ownsNode: true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return p;
    }

    public async Task ConnectAsync(string host, int port)
    {
        Status = $"connecting to {host}:{port}...";
        try
        {
            var result = await ElinkNode.JoinAsync(Node, host, port);
            IsConnected = result == MergeResult.Connected || result == MergeResult.AlreadyConnected;
            Status = IsConnected ? $"connected to {host}:{port}" : $"could not join {host}:{port}: {result}";
        }
        catch (Exception ex) { IsConnected = false; Status = "connect failed: " + ex.Message; }
    }

    public async Task DiscoverAsync(string peerName)
    {
        Status = $"looking for '{peerName}'...";
        try
        {
            var result = await ElinkNode.JoinByDiscoveryAsync(Node, peerName);
            IsConnected = result == MergeResult.Connected || result == MergeResult.AlreadyConnected;
            Status = IsConnected ? $"connected to '{peerName}'" : $"could not join '{peerName}': {result}";
        }
        catch (Exception ex) { IsConnected = false; Status = "discovery failed: " + ex.Message; }
    }

    public void Dispose() { if (_ownsNode) Node.Dispose(); }
}
