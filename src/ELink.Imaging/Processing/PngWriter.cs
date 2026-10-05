using System.IO.Compression;

namespace ELink.Imaging.Processing;

/// <summary>A minimal PNG encoder (grey or RGB, 8 or 16 bits per channel): enough to save and show a processed picture without any imaging library.</summary>
public static class PngWriter
{
    private static readonly uint[] Table = MakeTable();
    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data));
        s.Write(len);
    }

    /// <summary>Encodes planar float data (0..1, one plane per channel: 1 = grey, 3 = RGB), the first row on top.
    /// <paramref name="flipY"/> writes the rows last to first (FITS order).</summary>
    public static byte[] Encode(float[] planar, int width, int height, int channels, bool sixteenBit = false, bool flipY = false)
    {
        if (channels is not (1 or 3)) throw new ArgumentException("one or three channels");
        int bytesPer = sixteenBit ? 2 : 1, rowBytes = width * channels * bytesPer, plane = width * height;
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int src = (flipY ? height - 1 - y : y) * width, o = y * (rowBytes + 1);
            raw[o++] = 0;                                  // filter: none
            for (int x = 0; x < width; x++)
                for (int c = 0; c < channels; c++)
                {
                    double v = Math.Clamp(planar[c * plane + src + x], 0f, 1f);
                    if (sixteenBit) { ushort u = (ushort)Math.Round(v * 65535); raw[o++] = (byte)(u >> 8); raw[o++] = (byte)u; }
                    else raw[o++] = (byte)Math.Round(v * 255);
                }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)(sixteenBit ? 16 : 8); ihdr[9] = (byte)(channels == 3 ? 2 : 0);
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }
}
