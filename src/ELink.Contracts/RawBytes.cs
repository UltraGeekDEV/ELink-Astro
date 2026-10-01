using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts;

/// <summary>A length-prefixed byte blob (NON leaf "bytes"). EVent has no byte-array leaf, and a collection of
/// bytes costs five bytes per byte, so image frames and other binary payloads use this custom leaf.</summary>
public class RawBytes : IBinaryConvertible
{
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public RawBytes() { }
    public RawBytes(byte[] data) { Data = data; }

    public static implicit operator RawBytes(byte[] data) => new(data);

    public override string Name => "bytes";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;

    public override byte[] ToBytes()
    {
        var result = new byte[4 + Data.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), Data.Length);
        Data.CopyTo(result, 4);
        return result;
    }

    public override bool FromBytes(ref Span<byte> data)
    {
        if (data.Length < 4) return false;
        int length = BitConverter.ToInt32(data[..4]);
        if (length < 0 || data.Length - 4 < length) return false;
        Data = data.Slice(4, length).ToArray();
        data = data[(4 + length)..];
        return true;
    }
}
