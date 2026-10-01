using System.Text.RegularExpressions;

namespace ELink.Atlas;

/// <summary>Everything the atlas knows, loaded once: bright stars (KStars' Hipparcos/Tycho to about magnitude 8), deep-sky
/// objects (OpenNGC), constellation figures, and optionally faint stars from the GSC for small fields.</summary>
public sealed class AtlasCatalog
{
    public SkyIndex<CatalogStar> Stars { get; }
    public SkyIndex<CatalogDso> Dsos { get; }
    public ConstellationFigure Constellations { get; }
    public GscDeepStars? DeepStars { get; }
    public IReadOnlyList<CatalogStar> NamedStars { get; }
    public IReadOnlyList<CatalogDso> AllDsos { get; }

    /// <summary>The brightest magnitude GSC is consulted for: brighter stars come from the main catalogue.</summary>
    public const double DeepStarsFrom = 8.0;

    private AtlasCatalog(List<CatalogStar> stars, List<CatalogDso> dsos, ConstellationFigure figures, GscDeepStars? deep)
    {
        Stars = new SkyIndex<CatalogStar>(stars, s => (s.RaHours, s.DecDegrees));
        Dsos = new SkyIndex<CatalogDso>(dsos, d => (d.RaHours, d.DecDegrees));
        Constellations = figures; DeepStars = deep;
        NamedStars = stars.Where(s => s.Label != "").ToList();
        AllDsos = dsos;
    }

    /// <summary>Loads from the KStars data directory (and the GSC, when installed).</summary>
    public static AtlasCatalog LoadKStars(string dir = "/usr/share/kstars", string? gscData = "/usr/share/GSC")
    {
        string F(string n) => Path.Combine(dir, n);
        var stars = KStarsReaders.ReadStars(F("namedstars.dat"), F("starnames.dat"));
        if (File.Exists(F("unnamedstars.dat"))) stars.AddRange(KStarsReaders.ReadStars(F("unnamedstars.dat")));
        var byHd = stars.Where(s => s.Hd > 0).GroupBy(s => s.Hd).ToDictionary(g => g.Key, g => g.First());
        var figures = File.Exists(F("clines.dat")) && File.Exists(F("cnames.dat"))
            ? KStarsReaders.ReadConstellations(F("clines.dat"), F("cnames.dat"), byHd)
            : new ConstellationFigure(Array.Empty<((double, double), (double, double))>(), Array.Empty<(string, string, double, double)>());
        var dsos = File.Exists(F("OpenNGC.kscat")) ? OpenNgcReader.Read(F("OpenNGC.kscat")) : new List<CatalogDso>();
        GscDeepStars? deep = gscData is null ? null : new GscDeepStars(data: gscData);
        return new AtlasCatalog(stars, dsos, figures, deep is { Available: true } ? deep : null);
    }

    /// <summary>For tests and other sources: build from lists.</summary>
    public static AtlasCatalog From(List<CatalogStar> stars, List<CatalogDso> dsos, ConstellationFigure? figures = null) =>
        new(stars, dsos, figures ?? new ConstellationFigure(Array.Empty<((double, double), (double, double))>(), Array.Empty<(string, string, double, double)>()), null);

    // ---- search -------------------------------------------------------------------------------------------------

    private static readonly Regex Designation = new(@"^\s*(M|NGC|IC|C|SH2-?|B|LDN|ABELL|ARP|UGC|PGC)\s*-?\s*(\d+[A-Z]?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"M42", "m 42", "NGC1976", "Vega", "orion neb": exact designations first, then names that start with the text, then
    /// names that contain it.</summary>
    public List<(string Label, string Kind, string Detail, double Ra, double Dec, float Mag, float Size)> Search(string text, int max = 30)
    {
        var results = new List<(int Rank, (string, string, string, double, double, float, float) Hit)>();
        string q = text.Trim();
        if (q.Length == 0) return new();
        var m = Designation.Match(q);
        string? id = m.Success ? $"{m.Groups[1].Value.ToUpperInvariant().Replace("SH2", "Sh2")} {m.Groups[2].Value.ToUpperInvariant()}" : null;
        string compact = q.Replace(" ", "").ToUpperInvariant();

        foreach (var d in AllDsos)
        {
            int rank;
            if (id is not null && (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) || ContainsDesignation(d.CommonName, id))) rank = string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            else if (d.Id.Replace(" ", "").Equals(compact, StringComparison.OrdinalIgnoreCase)) rank = 0;
            else if (q.Length >= 3 && d.CommonName.StartsWith(q, StringComparison.OrdinalIgnoreCase)) rank = 2;
            else if (q.Length >= 3 && d.CommonName.Contains(q, StringComparison.OrdinalIgnoreCase)) rank = 3;
            else continue;
            results.Add((rank, (d.Id, d.Kind, d.CommonName, d.RaHours, d.DecDegrees, d.Magnitude, d.MajorArcmin)));
        }
        foreach (var s in NamedStars)
        {
            int rank;
            if (string.Equals(s.Label, q, StringComparison.OrdinalIgnoreCase)) rank = 0;
            else if (q.Length >= 2 && s.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase)) rank = 2;
            else if (q.Length >= 3 && s.Label.Contains(q, StringComparison.OrdinalIgnoreCase)) rank = 3;
            else continue;
            results.Add((rank, (s.Label, "Star", s.Hd > 0 ? $"HD {s.Hd}" : "", s.RaHours, s.DecDegrees, s.Magnitude, 0f)));
        }
        return results.OrderBy(r => r.Rank).ThenBy(r => float.IsNaN(r.Hit.Item6) ? 99 : r.Hit.Item6).Take(max).Select(r => r.Hit).ToList();
    }

    private static bool ContainsDesignation(string names, string id) =>
        names.Split(',').Any(n => string.Equals(n.Trim(), id, StringComparison.OrdinalIgnoreCase));
}
