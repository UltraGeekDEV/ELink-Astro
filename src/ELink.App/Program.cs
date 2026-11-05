using Avalonia;
using Avalonia.Fonts.Inter;
using ELink.Atlas;
using ELink.UI;
using Event.CoreFunctionality;

// ELink UI as its own process: joins an EVent mesh (a bridge, a composition host, ...) and shows what it finds. It carries the
// sky atlas itself (ELink's sky pack, shipped with it; or KStars' data), so the station it joins, a Pi say, does not have to.
//   elink-ui [--host 127.0.0.1] [--port 5698] [--sky-data PACK_OR_KSTARS_DIR | --no-atlas] [--gsc-data DIR]
namespace ELink.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string host = "127.0.0.1"; int port = 5698; string? skyData = null, gscData = null; bool atlas = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--sky-data" && i + 1 < args.Length) skyData = args[++i];
            else if (args[i] == "--gsc-data" && i + 1 < args.Length) gscData = args[++i];
            else if (args[i] == "--no-atlas") atlas = false;
            else { Console.WriteLine("usage: elink-ui [--host 127.0.0.1] [--port 5698] [--sky-data DIR | --no-atlas] [--gsc-data DIR]"); return args[i] is "-h" or "--help" ? 0 : 1; }
        }
        UI.App.Options = new UiLaunchOptions { Host = host, Port = port, LocalServices = atlas ? node => StartAtlasAsync(node, skyData, gscData) : null };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static readonly List<IAsyncDisposable> Hosted = new();

    /// <summary>Where KStars keeps its sky data on this computer (namedstars.dat and the deep-sky catalogues).</summary>
    public static string? FindSkyData(string? given)
    {
        var candidates = new List<string?> { given, Environment.GetEnvironmentVariable("ELINK_SKY_DATA") };
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (pf != "") candidates.Add(Path.Combine(pf, "KStars Desktop Planetarium", "bin", "data", "kstars"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kstars"));
        candidates.AddRange(["/usr/share/kstars", "/usr/local/share/kstars", "/Applications/KStars.app/Contents/Resources/kstars",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "kstars")]);
        return candidates.FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, "namedstars.dat")));
    }

    private static async Task<string> StartAtlasAsync(TypeSafeEVentNode node, string? skyData, string? gscData)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // ELink's own sky pack first (it ships with the app: no planetarium needed), then a KStars install on this computer
        string bundled = Path.Combine(AppContext.BaseDirectory, "sky", SkyPack.FileName);
        string? pack = skyData is not null && File.Exists(skyData) ? skyData : skyData is null && File.Exists(bundled) ? bundled : null;
        AtlasCatalog catalog; string from;
        if (pack is not null) { catalog = SkyPack.Read(pack); from = "ELink's sky pack"; }
        else
        {
            var dir = FindSkyData(skyData);
            if (dir is null) return "no sky atlas: the app's sky pack is missing and no KStars sky data was found (start with --sky-data PACK_OR_DIR)";
            catalog = AtlasCatalog.LoadKStars(dir, gscData ?? "/usr/share/GSC"); from = "KStars' data in " + dir;
        }
        var atlas = new AtlasService(node, catalog); await atlas.StartAsync(); Hosted.Add(atlas);
        var tonight = new TonightService(node, catalog); await tonight.StartAsync(); Hosted.Add(tonight);
        int faint = catalog.FaintStars?.Count ?? 0;
        return $"sky atlas: {catalog.Stars.Count + faint:N0} stars, {catalog.AllDsos.Count:N0} deep-sky objects, from {from} ({clock.Elapsed.TotalSeconds:0.0} s)";
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UI.App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
