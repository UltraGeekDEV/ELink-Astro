// Builds ELink's sky pack (src/ELink.App/sky/elink-sky.pack) from KStars' sky data and a GSC 1.2 dump, so the atlas needs no
// planetarium on the computer it runs on.
//   skypack KSTARS_DATA_DIR GSC_DUMP.txt OUT.pack
// The GSC dump is "ra_degrees dec_degrees magnitude" a line (tools/skypack/gscdump.py makes it with the INDI gsc tool).
using System.Globalization;
using ELink.Atlas;

if (args.Length == 2 && args[0] == "check")
{
    // skypack check PACK: what a small field round M42 gets
    var cat = SkyPack.Read(args[1]);
    var near = cat.FaintStars!.Cone(5.59, -5.4, 2).ToList();
    Console.WriteLine($"faint within 2 deg of M42: {near.Count}, mags {near.Min(s => s.Magnitude):0.0}..{near.Max(s => s.Magnitude):0.0}; first {near[0].RaHours:0.000}h {near[0].DecDegrees:0.000}");
    var bright = cat.Stars.Cone(5.59, -5.4, 2).ToList();
    Console.WriteLine($"bright within 2 deg: {bright.Count}, faintest {bright.Max(s => s.Magnitude):0.0}");
    return 0;
}
if (args.Length < 3) { Console.WriteLine("usage: skypack KSTARS_DATA_DIR GSC_DUMP.txt OUT.pack"); return 1; }
var clock = System.Diagnostics.Stopwatch.StartNew();
var catalog = AtlasCatalog.LoadKStars(args[0], gscData: null);
var faint = new List<SkyPack.FaintStar>();
foreach (var line in File.ReadLines(args[1]))
{
    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length < 3) continue;
    float ra = float.Parse(p[0], CultureInfo.InvariantCulture), dec = float.Parse(p[1], CultureInfo.InvariantCulture), mag = float.Parse(p[2], CultureInfo.InvariantCulture);
    if (mag <= AtlasCatalog.DeepStarsFrom) continue;          // the bright catalogue has those, with names and colours
    faint.Add(new SkyPack.FaintStar(ra / 15, dec, mag));
}
const string credits = "Stars: Hipparcos and Tycho-2 (ESA) as packaged by KStars (GPL); faint stars: the Guide Star Catalog 1.2 (STScI, public domain); " +
    "deep-sky objects: OpenNGC by Mattia Verga (CC-BY-SA 4.0); constellation figures and the Milky Way outline: KStars (GPL).";
SkyPack.Write(args[2], catalog, faint, credits);
Console.WriteLine($"{catalog.Stars.Count:N0} stars, {faint.Count:N0} faint stars, {catalog.AllDsos.Count:N0} deep-sky objects, {catalog.Constellations.Segments.Count} figure lines, " +
    $"{catalog.MilkyWay.Count} Milky Way contours -> {args[2]} ({new FileInfo(args[2]).Length / 1e6:0.0} MB) in {clock.Elapsed.TotalSeconds:0.0} s");
var back = SkyPack.Read(args[2]);
Console.WriteLine($"read back: {back.Stars.Count:N0} stars, {back.FaintStars?.Count ?? 0:N0} faint, {back.AllDsos.Count:N0} deep-sky objects");
return 0;
