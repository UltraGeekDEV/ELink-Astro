using System.IO.Compression;
using System.Text;

namespace ELink.Atlas;

/// <summary>ELink's own sky data, in one file that ships with the app: so the atlas works on a computer with no planetarium
/// installed. It holds the bright stars (Hipparcos/Tycho, with names and colours), faint stars to about magnitude 12 (GSC 1.2,
/// public domain), the deep-sky objects (OpenNGC, CC-BY-SA 4.0), the constellation figures and the Milky Way's outline (KStars,
/// GPL). Built by tools/skypack from those sources; read with <see cref="Read"/>.</summary>
public static class SkyPack
{
    private const string Magic = "ELSKYPK1";

    /// <summary>The file name the app looks for next to itself (in its "sky" folder).</summary>
    public const string FileName = "elink-sky.pack";

    /// <summary>Faint stars as packed: position and magnitude only (the GSC has no colours).</summary>
    public readonly record struct FaintStar(float RaHours, float DecDegrees, float Magnitude);

    public static void Write(string path, AtlasCatalog catalog, IReadOnlyList<FaintStar> faint, string credits)
    {
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.SmallestSize);
        using var w = new BinaryWriter(gz, Encoding.UTF8);
        w.Write(Magic);
        w.Write(credits);

        var stars = catalog.Stars.All().ToList();
        w.Write(stars.Count);
        foreach (var s in stars) { w.Write((float)s.RaHours); w.Write((float)s.DecDegrees); w.Write(s.Magnitude); w.Write(s.ColorIndex); w.Write(s.Hd); w.Write(s.Label); }

        // faint stars sorted by declination then RA: neighbours in the file are neighbours on the sky, which packs well
        var sorted = faint.OrderBy(f => (int)((f.DecDegrees + 90) * 2)).ThenBy(f => f.RaHours).ToList();
        w.Write(sorted.Count);
        foreach (var f in sorted)
        {
            w.Write((uint)Math.Round(f.RaHours / 24 * uint.MaxValue));                      // 0.02 milliarcseconds of RA
            w.Write((int)Math.Round(f.DecDegrees / 90 * int.MaxValue));
            w.Write((short)Math.Round(f.Magnitude * 100));
        }

        w.Write(catalog.AllDsos.Count);
        foreach (var d in catalog.AllDsos)
        {
            w.Write(d.Id); w.Write(d.Kind); w.Write(d.CommonName); w.Write(d.RaHours); w.Write(d.DecDegrees);
            w.Write(d.Magnitude); w.Write(d.MajorArcmin); w.Write(d.MinorArcmin); w.Write(d.PositionAngle);
        }

        w.Write(catalog.Constellations.Segments.Count);
        foreach (var (a, b) in catalog.Constellations.Segments) { w.Write(a.Ra); w.Write(a.Dec); w.Write(b.Ra); w.Write(b.Dec); }
        w.Write(catalog.Constellations.Labels.Count);
        foreach (var (abbr, name, ra, dec) in catalog.Constellations.Labels) { w.Write(abbr); w.Write(name); w.Write(ra); w.Write(dec); }

        w.Write(catalog.MilkyWay.Count);
        foreach (var shape in catalog.MilkyWay) { w.Write(shape.Count); foreach (var (ra, dec) in shape) { w.Write((float)ra); w.Write((float)dec); } }
    }

    /// <summary>The catalogue from a pack (faint stars kept apart: they are only fetched for small fields).</summary>
    public static AtlasCatalog Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8);
        if (r.ReadString() != Magic) throw new InvalidDataException($"{path} is not an ELink sky pack");
        string credits = r.ReadString();

        int n = r.ReadInt32();
        var stars = new List<CatalogStar>(n);
        for (int i = 0; i < n; i++) stars.Add(new CatalogStar(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), string.Intern(r.ReadString())));

        n = r.ReadInt32();
        var faint = new List<CatalogStar>(n);
        for (int i = 0; i < n; i++)
        {
            double ra = r.ReadUInt32() / (double)uint.MaxValue * 24, dec = r.ReadInt32() / (double)int.MaxValue * 90;
            faint.Add(new CatalogStar(ra, dec, r.ReadInt16() / 100f, float.NaN, 0, ""));
        }

        n = r.ReadInt32();
        var dsos = new List<CatalogDso>(n);
        for (int i = 0; i < n; i++)
            dsos.Add(new CatalogDso(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadDouble(), r.ReadDouble(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));

        n = r.ReadInt32();
        var segments = new List<((double, double), (double, double))>(n);
        for (int i = 0; i < n; i++) segments.Add(((r.ReadDouble(), r.ReadDouble()), (r.ReadDouble(), r.ReadDouble())));
        n = r.ReadInt32();
        var labels = new List<(string, string, double, double)>(n);
        for (int i = 0; i < n; i++) labels.Add((r.ReadString(), r.ReadString(), r.ReadDouble(), r.ReadDouble()));

        n = r.ReadInt32();
        var milkyWay = new List<IReadOnlyList<(double, double)>>(n);
        for (int i = 0; i < n; i++)
        {
            int m = r.ReadInt32(); var shape = new List<(double, double)>(m);
            for (int k = 0; k < m; k++) shape.Add((r.ReadSingle(), r.ReadSingle()));
            milkyWay.Add(shape);
        }
        return AtlasCatalog.FromPack(stars, faint, dsos, new ConstellationFigure(segments, labels), milkyWay, credits);
    }
}
