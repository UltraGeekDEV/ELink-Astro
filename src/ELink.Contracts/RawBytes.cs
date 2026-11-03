using System.Collections.Concurrent;
using System.Text;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts;

/// <summary>A length-prefixed byte blob (NON leaf "bytes"). EVent has no byte-array leaf, and a collection of
/// bytes costs five bytes per byte, so image frames and other binary payloads use this custom leaf.
/// <para>A large payload made with <see cref="Shared"/> travels as a reference: the bytes stay in this process's
/// <see cref="FrameStore"/> and every receiver in the same process gets the very same array (EVent would otherwise
/// copy a 48 MB frame twice for every subscriber of every hop). A receiver in another process gets the reference
/// with no bytes and fetches them over the mesh (<c>Frames.BytesAsync</c> in ELink.Core).</para></summary>
public class RawBytes : IBinaryConvertible
{
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>The <see cref="FrameStore"/> key of these bytes when they travel by reference; null otherwise.</summary>
    public string? Ref { get; set; }

    /// <summary>True when the bytes are elsewhere (another process) and have to be fetched.</summary>
    public bool Remote => Ref is not null && Data.Length == 0;

    public RawBytes() { }
    public RawBytes(byte[] data) { Data = data; }

    public static implicit operator RawBytes(byte[] data) => new(data);

    /// <summary>Bytes that travel by reference (kept in this process's frame store).</summary>
    public static RawBytes Shared(byte[] data) => new(data) { Ref = FrameStore.Put(data) };

    public override string Name => "bytes";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;

    // a reference: length -1, then the key's length and its UTF-8 bytes (a plain blob is never negative)
    public override byte[] ToBytes()
    {
        if (Ref is not null)
        {
            int n = Encoding.UTF8.GetByteCount(Ref);
            var r = new byte[8 + n];
            BitConverter.TryWriteBytes(r.AsSpan(0, 4), -1);
            BitConverter.TryWriteBytes(r.AsSpan(4, 4), n);
            Encoding.UTF8.GetBytes(Ref, r.AsSpan(8));
            return r;
        }
        var result = new byte[4 + Data.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), Data.Length);
        Data.CopyTo(result, 4);
        return result;
    }

    public override bool FromBytes(ref Span<byte> data)
    {
        if (data.Length < 4) return false;
        int length = BitConverter.ToInt32(data[..4]);
        if (length == -1)
        {
            if (data.Length < 8) return false;
            int n = BitConverter.ToInt32(data.Slice(4, 4));
            if (n < 0 || data.Length - 8 < n) return false;
            Ref = Encoding.UTF8.GetString(data.Slice(8, n));
            Data = FrameStore.TryGet(Ref) ?? Array.Empty<byte>();
            data = data[(8 + n)..];
            return true;
        }
        if (length < 0 || data.Length - 4 < length) return false;
        Data = data.Slice(4, length).ToArray();
        Ref = null;
        data = data[(4 + length)..];
        return true;
    }
}

/// <summary>The large payloads (frames) this process has handed out by reference. The newest ones are held for
/// receivers in other processes to fetch; older ones live as long as somebody here still uses them.</summary>
public static class FrameStore
{
    private static readonly ConcurrentDictionary<string, WeakReference<byte[]>> All = new();
    private static readonly LinkedList<(string Key, byte[] Data)> Recent = new();
    private static long _recentBytes;

    /// <summary>How many bytes of the newest payloads are kept for remote receivers (at least the newest one is).</summary>
    public static long KeepBytes { get; set; } = 64L * 1024 * 1024;

    public static string Put(byte[] data)
    {
        string key = Environment.ProcessId.ToString("x") + "-" + Guid.NewGuid().ToString("N");
        All[key] = new WeakReference<byte[]>(data);
        lock (Recent)
        {
            Recent.AddFirst((key, data)); _recentBytes += data.Length;
            while (Recent.Count > 1 && _recentBytes > KeepBytes) { _recentBytes -= Recent.Last!.Value.Data.Length; Recent.RemoveLast(); }
        }
        if (All.Count > 256)    // forget the keys of payloads nobody holds any more
            foreach (var kv in All) if (!kv.Value.TryGetTarget(out _)) All.TryRemove(kv.Key, out _);
        return key;
    }

    public static byte[]? TryGet(string key) => All.TryGetValue(key, out var w) && w.TryGetTarget(out var data) ? data : null;
}
