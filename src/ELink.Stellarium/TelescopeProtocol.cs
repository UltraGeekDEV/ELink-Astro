using System.Buffers.Binary;

namespace ELink.Stellarium;

/// <summary>Stellarium's telescope server protocol (Telescope Control plugin, "External software or a remote computer").
/// Little-endian. Client to server: goto, 20 bytes: length, type 0, client time (µs), RA, Dec. Server to client: current
/// position, 24 bytes: length, type 0, server time (µs), RA, Dec, status. RA: 0x100000000 = 24 h (unsigned, wraps);
/// Dec: ±0x40000000 = ±90°. Coordinates are J2000.</summary>
public static class TelescopeProtocol
{
    public const int GotoLength = 20, PositionLength = 24;

    public static uint EncodeRa(double raHours) => (uint)Math.Round(((raHours % 24 + 24) % 24) / 24.0 * 4294967296.0) ;
    public static int EncodeDec(double decDegrees) => (int)Math.Round(Math.Clamp(decDegrees, -90, 90) / 90.0 * 0x40000000);
    public static double DecodeRa(uint ra) => ra / 4294967296.0 * 24.0;
    public static double DecodeDec(int dec) => dec / (double)0x40000000 * 90.0;

    public static long Microseconds(DateTime utc) => (utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / 10;

    public static byte[] Position(double raHours, double decDegrees, int status = 0, DateTime? at = null)
    {
        var b = new byte[PositionLength];
        BinaryPrimitives.WriteUInt16LittleEndian(b, PositionLength);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 0);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), Microseconds(at ?? DateTime.UtcNow));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), EncodeRa(raHours));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), EncodeDec(decDegrees));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), status);
        return b;
    }

    public static byte[] Goto(double raHours, double decDegrees, DateTime? at = null)
    {
        var b = new byte[GotoLength];
        BinaryPrimitives.WriteUInt16LittleEndian(b, GotoLength);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 0);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), Microseconds(at ?? DateTime.UtcNow));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), EncodeRa(raHours));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), EncodeDec(decDegrees));
        return b;
    }

    /// <summary>Parses one message from the front of the buffer. Returns the bytes used (0 = need more data).</summary>
    public static int TryParse(ReadOnlySpan<byte> data, out ushort type, out double raHours, out double decDegrees)
    {
        type = 0; raHours = decDegrees = 0;
        if (data.Length < 4) return 0;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (length < 4) return Math.Min(data.Length, 2);                     // garbage: skip a little and resynchronise
        if (data.Length < length) return 0;
        type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (type == 0 && length >= GotoLength)
        {
            raHours = DecodeRa(BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
            decDegrees = DecodeDec(BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
        }
        return length;
    }
}
