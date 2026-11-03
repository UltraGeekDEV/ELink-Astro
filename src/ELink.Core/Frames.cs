using ELink.Contracts;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Core;

/// <summary>Frames that travel by reference (<see cref="RawBytes.Shared"/>): every node serves the ones its process holds,
/// and a receiver in another process fetches the bytes over the mesh when it needs them.</summary>
public static class Frames
{
    public const string GetId = "ELink.Frames.Get";

    /// <summary>Answers <see cref="GetId"/> for the frames of this process (an empty answer when it does not have one).</summary>
    public static void Serve(TypeSafeEVentNode node) =>
        node.RegisterFunction<BinaryConvertibleString, RawBytes>(GetId, key => new RawBytes(FrameStore.TryGet(key.Text) ?? Array.Empty<byte>()),
            "the bytes of a frame this process handed out by reference (empty if it is gone)");

    /// <summary>The bytes, from here when they are here, otherwise from whichever node has them. Empty when nobody has them any more.</summary>
    public static async Task<byte[]> BytesAsync(TypeSafeEVentNode node, RawBytes raw, TimeSpan? timeout = null)
    {
        if (!raw.Remote) return raw.Data;
        if (FrameStore.TryGet(raw.Ref!) is { } local) return raw.Data = local;
        var answers = await node.CallFunctionAsync<BinaryConvertibleString, RawBytes>(GetId, (BinaryConvertibleString)raw.Ref!, timeout ?? TimeSpan.FromSeconds(300));
        var found = answers?.FirstOrDefault(a => a.Data.Length > 0)?.Data ?? Array.Empty<byte>();
        if (found.Length > 0) raw.Data = found;
        return found;
    }
}
