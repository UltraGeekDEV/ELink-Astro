using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ELink.Atlas;
using ELink.Contracts.Atlas;
using ELink.Core;
using ELink.UI.Controls;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The chart drawn from ELink's own sky pack (no planetarium installed): the Milky Way's glow, star colours and sizes,
/// faint stars when zoomed in, nebulae and galaxies as glows. Saves the pictures with ELINK_SCREENSHOTS=dir.</summary>
public class SkyAtlasLookTests
{
    private static string? PackPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "ELink.App", "sky", SkyPack.FileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static AtlasCatalog? _catalog;
    private static AtlasCatalog? Catalog() => _catalog ??= PackPath() is { } p ? SkyPack.Read(p) : null;

    private static async Task<SkyChart> ChartAsync(AtlasService atlas, SkyGlowMap glow, double ra, double dec, double fov)
    {
        double limit = ELink.UI.ViewModels.AtlasViewModel.StarLimitFor(fov);
        var chunk = await atlas.QueryAsync(new AtlasQuery
        {
            RaHours = ra, DecDegrees = dec, RadiusDegrees = Math.Min(180, fov * 0.8), StarMagnitudeLimit = limit,
            DsoMagnitudeLimit = Math.Clamp(limit + 2.5, 8, 16), MaxStars = 60000, MaxDsos = 3000,
        });
        return new SkyChart
        {
            CenterRa = ra, CenterDec = dec, Fov = fov, StarLimit = limit, ShowGrid = false, ShowConstellations = fov > 30, SkyGlow = glow,
            Stars = chunk.Stars.Select(s => new ChartStar(s.RaHours.value, s.DecDegrees.value, s.Magnitude.value, s.ColorIndex.value, s.Label.Text)).ToList(),
            Dsos = chunk.Dsos.Select(d => new ChartDso(d.Id.Text, d.Kind.Text, d.CommonName.Text, d.RaHours.value, d.DecDegrees.value, d.Magnitude.value, d.MajorArcmin.value, d.MinorArcmin.value, d.PositionAngle.value)).ToList(),
        };
    }

    [AvaloniaFact]
    public async Task ThePackDrawsARealisticSky()
    {
        var catalog = Catalog();
        if (catalog is null) return;                         // the pack is built by tools/skypack; without it there is nothing to look at
        Assert.True(catalog.Stars.Count > 30000);
        Assert.True(catalog.FaintStars?.Count > 1_000_000, "faint stars");
        Assert.True(catalog.MilkyWay.Count > 50, "Milky Way outline");
        var glow = SkyGlowMap.FromBytes(AtlasCatalog.GlowWidth, AtlasCatalog.GlowHeight, catalog.GlowMap());
        using var node = ElinkNode.Create("atlas-look-" + Guid.NewGuid().ToString("N")[..6], 0);
        var atlas = new AtlasService(node, catalog);
        string? dir = Environment.GetEnvironmentVariable("ELINK_SCREENSHOTS");

        foreach (var (name, ra, dec, fov) in new[] { ("cygnus-milky-way", 20.4, 40.0, 70.0), ("scutum-sagittarius", 18.6, -22.0, 60.0), ("orion", 5.55, -2.0, 24.0), ("m42", 5.59, -5.4, 2.5),
                                                     ("pleiades", 3.79, 24.1, 3.0), ("andromeda", 0.712, 41.27, 5.0), ("lagoon-trifid", 18.05, -23.5, 4.0) })
        {
            var chart = await ChartAsync(atlas, glow, ra, dec, fov);
            var w = new Window { Width = 1400, Height = 900, Content = chart };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var frame = w.CaptureRenderedFrame()!;
            if (dir is not null) { Directory.CreateDirectory(dir); frame.Save(Path.Combine(dir, $"atlas-{name}.png")); }
            if (name == "pleiades") Assert.True(chart.Stars!.Count > 300, $"{chart.Stars.Count} stars round the Pleiades: the faint ones should be there");
            w.Close();
        }
    }
}
