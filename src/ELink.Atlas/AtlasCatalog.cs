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
    /// <summary>Faint stars held in memory (a sky pack's GSC, to about magnitude 12), for fields small enough to want them.</summary>
    public SkyIndex<CatalogStar>? FaintStars { get; private init; }
    /// <summary>Where the data came from, for the about box.</summary>
    public string Credits { get; private init; } = "";
    public IReadOnlyList<CatalogStar> NamedStars { get; }
    public IReadOnlyList<CatalogDso> AllDsos { get; }
    /// <summary>The Milky Way's brightness contours (closed outlines, RA hours / Dec degrees), empty when the data has none.</summary>
    public IReadOnlyList<IReadOnlyList<(double Ra, double Dec)>> MilkyWay { get; private init; } = Array.Empty<IReadOnlyList<(double, double)>>();

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
        return new AtlasCatalog(stars, dsos, figures, deep is { Available: true } ? deep : null) { MilkyWay = File.Exists(F("milkyway.dat")) ? ReadMilkyWay(F("milkyway.dat")) : Array.Empty<IReadOnlyList<(double, double)>>() };
    }

    /// <summary>KStars' milkyway.dat: "M ra dec" or "S ra dec" starts a contour, "D ra dec" continues it (RA in hours).</summary>
    public static List<IReadOnlyList<(double Ra, double Dec)>> ReadMilkyWay(string path)
    {
        var shapes = new List<IReadOnlyList<(double, double)>>(); List<(double, double)>? cur = null;
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ra)
                || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dec)) continue;
            if (p[0] is "M" or "S" || cur is null) { if (cur is { Count: > 2 }) shapes.Add(cur); cur = new(); }
            cur.Add((ra, dec));
        }
        if (cur is { Count: > 2 }) shapes.Add(cur);
        return shapes;
    }

    /// <summary>From a sky pack (see <see cref="SkyPack"/>).</summary>
    public static AtlasCatalog FromPack(List<CatalogStar> stars, List<CatalogStar> faint, List<CatalogDso> dsos, ConstellationFigure figures,
        IReadOnlyList<IReadOnlyList<(double, double)>> milkyWay, string credits) =>
        new(stars, dsos, figures, null)
        {
            FaintStars = faint.Count > 0 ? new SkyIndex<CatalogStar>(faint, s => (s.RaHours, s.DecDegrees)) : null, MilkyWay = milkyWay, Credits = credits,
        };

    private byte[]? _glow;
    public const int GlowWidth = 1440, GlowHeight = 720;

    /// <summary>The Milky Way's glow as an all-sky map (equirectangular, RA 0h..24h across, Dec +90..-90 down; 0..255), made from
    /// how densely the faint stars crowd the sky: the band is the light of stars too faint to see one by one, so where faint
    /// stars are many it glows, and the dust lanes (the Great Rift) show as the gaps. The faint stars of a sky pack (to about
    /// magnitude 12) trace it well; a KStars catalogue (to 8) only roughly.</summary>
    public byte[] GlowMap() => _glow ??= MakeGlow(FaintStars?.All() ?? Stars.All().Where(s => s.Magnitude > 6));

    private static byte[] MakeGlow(IEnumerable<CatalogStar> stars)
    {
        int w = GlowWidth, h = GlowHeight;
        var acc = new double[w * h];
        foreach (var s in stars)
        {
            int c = (int)((s.RaHours % 24 + 24) % 24 / 24 * w) % w, r = Math.Clamp((int)((90 - s.DecDegrees) / 180 * h), 0, h - 1);
            acc[r * w + c] += 1;
        }
        // per unit of sky: a cell near a pole covers less of it
        for (int r = 0; r < h; r++)
        {
            double area = Math.Max(Math.Cos((90 - (r + 0.5) * 180 / h) * Math.PI / 180), 0.02);
            for (int c = 0; c < w; c++) acc[r * w + c] /= area;
        }
        // smooth over about a degree (wider in RA near the poles, where its cells are narrow)
        var tmp = new double[acc.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int r = 0; r < h; r++)
            {
                double cosd = Math.Max(Math.Cos((90 - (r + 0.5) * 180 / h) * Math.PI / 180), 0.02);
                int rad = Math.Min(w / 2 - 1, (int)Math.Ceiling(3 / cosd));
                double sum = 0; int row = r * w;
                for (int k = -rad; k <= rad; k++) sum += acc[row + ((k % w) + w) % w];
                for (int c = 0; c < w; c++) { tmp[row + c] = sum / (2 * rad + 1); sum += acc[row + (c + rad + 1) % w] - acc[row + ((c - rad) % w + w) % w]; }
            }
            for (int c = 0; c < w; c++)
                for (int r = 0; r < h; r++)
                {
                    double sum = 0; int n = 0;
                    for (int k = Math.Max(0, r - 3); k <= Math.Min(h - 1, r + 3); k++) { sum += tmp[k * w + c]; n++; }
                    acc[r * w + c] = sum / n;
                }
        }
        // the band against the rest of the sky: the darkest skies (the galactic poles) are nothing, the richest star clouds full
        var sorted = acc.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return new byte[w * h];
        double floor = sorted[(int)(sorted.Length * 0.6)], top = sorted[(int)(sorted.Length * 0.998)];
        var glow = new byte[w * h];
        for (int i = 0; i < acc.Length; i++)
        {
            double v = Math.Clamp((acc[i] - floor) / Math.Max(top - floor, 1e-9), 0, 1);
            glow[i] = (byte)Math.Round(255 * Math.Pow(v, 1.25));
        }
        return glow;
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
