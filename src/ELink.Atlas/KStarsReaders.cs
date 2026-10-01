using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ELink.Atlas;

/// <summary>Reads the sky data KStars installs (usually /usr/share/kstars): the binary star catalogues (namedstars.dat,
/// unnamedstars.dat with starnames.dat) and the constellation figures (clines.dat, cnames.dat). The data stays where it is;
/// nothing is copied into ELink.</summary>
public static class KStarsReaders
{
    /// <summary>Header of KStars' binary data files (auxiliary/binfilehelper): 124 bytes of text, an endianness mark, a version,
    /// the field table and an index of (trixel id, offset, record count).</summary>
    private sealed record BinHeader(int RecordSize, long DataOffset, uint[] RecordsPerTrixel, bool Swap);

    private static BinHeader ReadHeader(BinaryReader r)
    {
        r.ReadBytes(124);
        ushort endian = r.ReadUInt16();
        bool swap = endian != 0x4B53;                                  // "SK" read as little-endian
        ushort U16(ushort v) => swap ? BinaryPrimitives.ReverseEndianness(v) : v;
        uint U32(uint v) => swap ? BinaryPrimitives.ReverseEndianness(v) : v;
        r.ReadByte();                                                  // version
        int fields = U16(r.ReadUInt16());
        int recordSize = 0;
        for (int i = 0; i < fields; i++)
        {
            var de = r.ReadBytes(16);                                  // name[10], size, type, scale[4]
            recordSize += de[10];
        }
        uint indexSize = U32(r.ReadUInt32());
        var counts = new uint[indexSize];
        for (int i = 0; i < indexSize; i++)
        {
            uint id = U32(r.ReadUInt32()); r.ReadUInt32(); uint n = U32(r.ReadUInt32());
            if (id < indexSize) counts[id] = n;
        }
        return new BinHeader(recordSize, r.BaseStream.Position, counts, swap);
    }

    /// <summary>All stars of a KStars star file. Named stars take their names, in order, from the names file.</summary>
    public static List<CatalogStar> ReadStars(string starFile, string? namesFile = null)
    {
        using var fs = File.OpenRead(starFile);
        using var r = new BinaryReader(fs);
        var h = ReadHeader(r);
        if (h.RecordSize != 32) throw new FormatException($"{Path.GetFileName(starFile)}: expected 32-byte star records, found {h.RecordSize}");
        r.ReadBytes(5);                                                // faint magnitude, HTM level, unused

        BinaryReader? names = null; FileStream? nfs = null;
        if (namesFile is not null && File.Exists(namesFile))
        {
            nfs = File.OpenRead(namesFile); names = new BinaryReader(nfs);
            ReadHeader(names);
        }
        try
        {
            var stars = new List<CatalogStar>();
            var rec = new byte[32];
            foreach (uint count in h.RecordsPerTrixel)
                for (uint i = 0; i < count; i++)
                {
                    if (fs.Read(rec) != 32) return stars;
                    int I32(int o) => h.Swap ? BinaryPrimitives.ReadInt32BigEndian(rec.AsSpan(o)) : BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(o));
                    short I16(int o) => h.Swap ? BinaryPrimitives.ReadInt16BigEndian(rec.AsSpan(o)) : BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(o));
                    double ra = I32(0) / 1_000_000.0, dec = I32(4) / 100_000.0;
                    int hd = I32(20);
                    float mag = I16(24) / 100f, bv = I16(26) / 100f;
                    byte flags = rec[30];
                    string label = "";
                    if ((flags & 0x01) != 0 && names is not null)
                    {
                        var nm = names.ReadBytes(40);
                        string bayer = Latin1(nm.AsSpan(0, 8)), longName = Latin1(nm.AsSpan(8, 32));
                        label = longName != "" ? longName : bayer.StartsWith('.') ? "" : bayer;
                    }
                    stars.Add(new CatalogStar(ra, dec, mag, bv, hd, label));
                }
            return stars;
        }
        finally { names?.Dispose(); nfs?.Dispose(); }
    }

    private static string Latin1(ReadOnlySpan<byte> b)
    {
        int end = b.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? b : b[..end]).Trim();
    }

    /// <summary>Constellation figures: clines.dat joins stars by HD number ("M hd" starts a polyline, "D hd" draws to the next);
    /// cnames.dat gives each constellation's label position.</summary>
    public static ConstellationFigure ReadConstellations(string clinesFile, string cnamesFile, IReadOnlyDictionary<int, CatalogStar> byHd)
    {
        var segments = new List<((double, double), (double, double))>();
        (double, double)? last = null;
        bool western = true;
        foreach (var raw in File.ReadLines(clinesFile))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == 'C') { western = line.Contains("Western", StringComparison.OrdinalIgnoreCase); last = null; continue; }
            if (!western || line.Length < 3) continue;
            if (!int.TryParse(line[2..].Trim(), out int hd)) continue;
            if (!byHd.TryGetValue(hd, out var star)) { last = null; continue; }
            var p = (star.RaHours, star.DecDegrees);
            if (line[0] == 'D' && last is { } prev) segments.Add((prev, p));
            last = p;
        }
        var labels = new List<(string, string, double, double)>();
        bool westernNames = true;
        foreach (var raw in File.ReadLines(cnamesFile))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == 'C') { westernNames = line.Contains("Western", StringComparison.OrdinalIgnoreCase); continue; }
            if (!westernNames || line.Length < 17) continue;
            // 012000+410000And ANDROMEDA : hhmmss, sddmmss, abbreviation, name
            if (!int.TryParse(line[0..2], out int rh) || !int.TryParse(line[2..4], out int rm) || !int.TryParse(line[4..6], out int rs)) continue;
            int sign = line[6] == '-' ? -1 : 1;
            if (!int.TryParse(line[7..9], out int dd) || !int.TryParse(line[9..11], out int dm) || !int.TryParse(line[11..13], out int ds)) continue;
            string abbr = line[13..16];
            string name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(line[16..].Trim().ToLowerInvariant());
            labels.Add((abbr, name, rh + rm / 60.0 + rs / 3600.0, sign * (dd + dm / 60.0 + ds / 3600.0)));
        }
        return new ConstellationFigure(segments, labels);
    }
}
