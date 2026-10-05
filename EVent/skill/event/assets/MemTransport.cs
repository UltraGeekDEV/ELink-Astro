// In-memory EVent transport for tests and simulations. Drop this file into a test project that references EVent.
// It uses only EVent's public API: MemStream is an IStreamClient, MemProtocol an ICommsProtocol, so nodes built on it
// run the real framing, handshake, routing and NOTES code; only the bytes travel through memory.
//
//   var a = new TypeSafeEVentNode("A", new MemProtocol("A"));
//   var b = new TypeSafeEVentNode("B", new MemProtocol("B"));
//   b.TryEstablishConnection(MemProtocol.To("A"));          // B dials A
//
// MemProtocol names are process-global: use unique names per test, and Stop() nodes when done.

using System.Collections.Concurrent;
using System.Threading.Channels;
using EVent.Connections;
using EVent.Connections.Models;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.CoreFunctionality;

namespace EVent.Testing;

/// <summary>
/// One end of an in-memory, full-duplex link. Every Send delivers one serialized package to the peer, which parses it
/// with the library's own Package.ReadPackage, so framing is exercised for real. Fault injection: Kill (cable pull),
/// BreakWrites (half-open link), InjectIncoming (a hostile or buggy peer), LatencyMs and SendDelayMs.
/// </summary>
public sealed class MemStream : IStreamClient
{
    private readonly Channel<byte[]> inbox = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<(long due, byte[] data)> delayed = Channel.CreateUnbounded<(long, byte[])>();
    private int delayPumpStarted;

    public MemStream? Peer { get; private set; }
    public string Name { get; }
    public volatile bool Closed;
    /// <summary>Half-open link: our writes fail, but we keep reading.</summary>
    public volatile bool BreakWrites;
    /// <summary>Pipelined one-way latency: each frame arrives this much later, in order; bandwidth is unaffected.</summary>
    public int LatencyMs;
    /// <summary>Per-frame send delay that holds up the next frame: a slow link.</summary>
    public int SendDelayMs;

    private MemStream(string name) { Name = name; }

    /// <summary>Two connected ends.</summary>
    public static (MemStream a, MemStream b) Pair(string a, string b)
    {
        var x = new MemStream(a + "->" + b);
        var y = new MemStream(b + "->" + a);
        x.Peer = y;
        y.Peer = x;
        return (x, y);
    }

    public async Task Send(byte[] data)
    {
        if (BreakWrites) throw new IOException("simulated write failure (half-open link)");
        if (Closed || Peer == null || Peer.Closed) throw new IOException("MemStream closed");
        if (SendDelayMs > 0) await Task.Delay(SendDelayMs);
        if (LatencyMs > 0)
        {
            delayed.Writer.TryWrite((Environment.TickCount64 + LatencyMs, data));
            if (Interlocked.Exchange(ref delayPumpStarted, 1) == 0) _ = DelayPump();
            return;
        }
        Peer.inbox.Writer.TryWrite(data);
    }

    private async Task DelayPump()
    {
        await foreach (var (due, data) in delayed.Reader.ReadAllAsync())
        {
            long wait = due - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait);
            if (Peer != null && !Peer.Closed) Peer.inbox.Writer.TryWrite(data);
        }
    }

    /// <summary>Delivers raw bytes to this side, as if the peer had sent them (a whole frame, or garbage).</summary>
    public void InjectIncoming(byte[] raw) => inbox.Writer.TryWrite(raw);

    public async Task<Package?> ReadPackage()
    {
        try
        {
            var bytes = await inbox.Reader.ReadAsync();
            using var ms = new MemoryStream(bytes);
            return await Package.ReadPackage(ms);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Like closing a socket: our side stops, and the peer sees end-of-stream on its next read.</summary>
    public void Close()
    {
        Closed = true;
        inbox.Writer.TryComplete();
        Peer?.inbox.Writer.TryComplete();
    }

    /// <summary>A cable pull: both directions die.</summary>
    public void Kill()
    {
        Close();
        Peer?.Close();
    }
}

/// <summary>A direct link between two plain EVentNodes (no transport, no loop check: keep the graph a tree).</summary>
public sealed class MemLink
{
    public MemStream A = null!, B = null!;
    public QueuedClient ClientOnA = null!, ClientOnB = null!;
    public void Kill() => A.Kill();

    public static MemLink Connect(EVentNode a, EVentNode b)
    {
        var (sa, sb) = MemStream.Pair(a.NodeID, b.NodeID);
        var link = new MemLink { A = sa, B = sb, ClientOnA = new QueuedClient(sa), ClientOnB = new QueuedClient(sb) };
        a.AcceptClient(link.ClientOnA);
        b.AcceptClient(link.ClientOnB);
        return link;
    }
}

/// <summary>
/// An ICommsProtocol over MemStream. A node with <c>new MemProtocol("A")</c> can be dialed with <c>MemProtocol.To("A")</c>
/// (TypeSafeEVentNode.TryEstablishConnection or a CreateInterconnect admin package).
/// </summary>
public sealed class MemProtocol : ICommsProtocol
{
    public const string Type = "MEM";
    private static readonly ConcurrentDictionary<string, MemProtocol> registry = new();

    public string Name { get; }
    /// <summary>Every stream this protocol dialed, for fault injection in tests.</summary>
    public ConcurrentBag<MemStream> Streams { get; } = new();
    private Action<QueuedClient>? accepted;

    public MemProtocol(string name) { Name = name; }

    public void OnClientAccepted(Action<QueuedClient> action) => accepted += action;
    public void Run() => registry[Name] = this;
    public void Stop() => registry.TryRemove(Name, out _);

    public Task<QueuedClient?> EstablishInterconnect(Package package)
    {
        var info = new Package();
        Span<byte> bytes = package.Data;
        if (!info.FromBytes(ref bytes) || info.EventID != Type) return Task.FromResult<QueuedClient?>(null);
        var target = new BinaryConvertibleString();
        Span<byte> payload = info.Data;
        if (!target.FromBytes(ref payload)) return Task.FromResult<QueuedClient?>(null);
        if (!registry.TryGetValue(target.Text, out var remote)) return Task.FromResult<QueuedClient?>(null);
        var (mine, theirs) = MemStream.Pair(Name, remote.Name);
        Streams.Add(mine);
        remote.Streams.Add(theirs);
        remote.accepted?.Invoke(new QueuedClient(theirs));
        return Task.FromResult<QueuedClient?>(new QueuedClient(mine));
    }

    /// <summary>The connection info to dial the MemProtocol named <paramref name="target"/>.</summary>
    public static Package To(string target) => new Package(Type, target);
}
