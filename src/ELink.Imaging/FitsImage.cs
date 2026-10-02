using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ELink.Imaging;

/// <summary>A decoded FITS image: planar float samples with BZERO/BSCALE applied (so 16-bit data is 0..65535).</summary>
public sealed class FitsImage
{
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    /// <summary>Planar: channel c, row y, column x at [c * Width * Height + y * Width + x]. Row 0 is the bottom of the image, as in FITS.</summary>
    public float[] Data { get; }
    public IReadOnlyDictionary<string, string> Header { get; }
    /// <summary>The largest value the sensor/format can produce (65535 for 16-bit, 255 for 8-bit), 1 for float data.</summary>
    public double Range { get; }
    /// <summary>Row 0 is the top of the picture (ROWORDER = 'TOP-DOWN', as INDI and most capture software write it).</summary>
    public bool TopDown => Header.TryGetValue("ROWORDER", out var o) && o.StartsWith("TOP", StringComparison.OrdinalIgnoreCase);

    private FitsImage(int w, int h, int c, float[] data, Dictionary<string, string> header, double range)
    { Width = w; Height = h; Channels = c; Data = data; Header = header; Range = range; }

    public string? Get(string key) => Header.TryGetValue(key, out var v) ? v : null;
    public double GetDouble(string key, double fallback = double.NaN) =>
        Get(key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    public static FitsImage Parse(ReadOnlySpan<byte> bytes)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        int pos = 0; bool ended = false;
        while (!ended)
        {
            if (pos + 2880 > bytes.Length) throw new FormatException("FITS header is not terminated");
            for (int card = 0; card < 36 && !ended; card++)
            {
                var line = Encoding.ASCII.GetString(bytes.Slice(pos + card * 80, 80));
                string key = line[..8].TrimEnd();
                if (key == "END") { ended = true; break; }
                if (line.Length > 10 && line[8] == '=')
                {
                    string value = line[10..];
                    int slash = value.IndexOf('/');
                    // strings may contain '/' and doubled quotes (''), so only cut a comment after the real closing quote
                    if (value.TrimStart().StartsWith('\''))
                    {
                        int open = value.IndexOf('\'');
                        var sb = new StringBuilder();
                        int j = open + 1;
                        for (; j < value.Length; j++)
                        {
                            if (value[j] == '\'')
                            {
                                if (j + 1 < value.Length && value[j + 1] == '\'') { sb.Append('\''); j++; continue; }
                                break;
                            }
                            sb.Append(value[j]);
                        }
                        value = sb.ToString().TrimEnd();
                    }
                    else value = (slash >= 0 ? value[..slash] : value).Trim();
                    header[key] = value;
                }
            }
            pos += 2880;
        }

        int Int(string k, int fallback = 0) => header.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : fallback;
        double Dbl(string k, double fallback) => header.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        int bitpix = Int("BITPIX"), naxis = Int("NAXIS");
        if (naxis < 2 || naxis > 3) throw new NotSupportedException($"NAXIS={naxis} is not an image");
        int w = Int("NAXIS1"), h = Int("NAXIS2"), c = naxis == 3 ? Int("NAXIS3", 1) : 1;
        if (w <= 0 || h <= 0 || c <= 0 || c > 4) throw new FormatException("bad image dimensions");
        double bzero = Dbl("BZERO", 0), bscale = Dbl("BSCALE", 1);
        int bytesPer = Math.Abs(bitpix) / 8;
        long count = (long)w * h * c;
        if (bytesPer == 0 || pos + count * bytesPer > bytes.Length) throw new FormatException("FITS data is truncated");

        var data = new float[count];
        var src = bytes.Slice(pos);
        for (long i = 0; i < count; i++)
        {
            double raw = bitpix switch
            {
                8 => src[(int)i],
                16 => BinaryPrimitives.ReadInt16BigEndian(src.Slice((int)i * 2, 2)),
                32 => BinaryPrimitives.ReadInt32BigEndian(src.Slice((int)i * 4, 4)),
                -32 => BinaryPrimitives.ReadSingleBigEndian(src.Slice((int)i * 4, 4)),
                -64 => BinaryPrimitives.ReadDoubleBigEndian(src.Slice((int)i * 8, 8)),
                _ => throw new NotSupportedException($"BITPIX={bitpix}"),
            };
            data[i] = (float)(bzero + bscale * raw);
        }
        double range = bitpix switch { 8 => 255, 16 => 65535, 32 => 4294967295.0, _ => 1 };
        if (bitpix < 0)
        {
            // float data is usually 0..1 or 0..65535: find out
            float max = 0; foreach (var v in data) if (v > max) max = v;
            range = max > 1.5f ? Math.Max(max, 1) : 1;
        }
        return new FitsImage(w, h, c, data, header, range);
    }

    /// <summary>Builds a 16-bit mono FITS file (for tests and for saving frames).</summary>
    public static byte[] Write16(int width, int height, ushort[] pixels, IDictionary<string, string>? extra = null)
    {
        var cards = new List<string>
        {
            Card("SIMPLE", "T"), Card("BITPIX", "16"), Card("NAXIS", "2"), Card("NAXIS1", width.ToString()),
            Card("NAXIS2", height.ToString()), Card("BZERO", "32768"), Card("BSCALE", "1"),
        };
        if (extra is not null) foreach (var kv in extra) cards.Add(Card(kv.Key, kv.Value));
        cards.Add("END".PadRight(80));
        var header = string.Concat(cards).PadRight((cards.Count * 80 + 2879) / 2880 * 2880);
        var data = new byte[(pixels.Length * 2 + 2879) / 2880 * 2880];
        for (int i = 0; i < pixels.Length; i++) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), unchecked((short)(pixels[i] - 32768)));
        return Encoding.ASCII.GetBytes(header).Concat(data).ToArray();
    }

    /// <summary>Builds a 32-bit float mono FITS file (BITPIX -32); NaN pixels are written as <paramref name="blank"/>.</summary>
    public static byte[] WriteFloat32(int width, int height, float[] pixels, IEnumerable<(string Key, string Value)>? extra = null, float blank = float.NaN)
    {
        var cards = new List<string>
        {
            Card("SIMPLE", "T"), Card("BITPIX", "-32"), Card("NAXIS", "2"), Card("NAXIS1", width.ToString()), Card("NAXIS2", height.ToString()),
        };
        if (extra is not null) foreach (var (k, v) in extra) cards.Add(Card(k, v));
        cards.Add("END".PadRight(80));
        var header = Encoding.ASCII.GetBytes(string.Concat(cards).PadRight((cards.Count * 80 + 2879) / 2880 * 2880));
        long n = (long)width * height;
        var bytes = new byte[header.Length + (n * 4 + 2879) / 2880 * 2880];
        header.CopyTo(bytes, 0);
        for (long i = 0; i < n; i++)
        {
            float v = pixels[i];
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan((int)(header.Length + i * 4), 4), float.IsNaN(v) ? blank : v);
        }
        return bytes;
    }

    private static string Card(string key, string value) => (key.PadRight(8) + "= " + value.PadLeft(20)).PadRight(80);
}
