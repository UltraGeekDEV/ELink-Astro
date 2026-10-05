using ELink.Atlas;
using ELink.Contracts.Atlas;
using ELink.Core;
using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests.Atlas;

/// <summary>Tonight's best: which objects are well placed in the dark hours, and which suit the frame.</summary>
public class TonightTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private TypeSafeEVentNodeHolder _h = null!;
    private sealed class TypeSafeEVentNodeHolder : IDisposable
    {
        public Event.CoreFunctionality.TypeSafeEVentNode Node = ElinkNode.Create("TN-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        public void Dispose() => Node.Dispose();
    }
    public Task InitializeAsync() { _h = new(); return Task.CompletedTask; }
    public Task DisposeAsync() { _h.Dispose(); return Task.CompletedTask; }

    private static readonly GeoSite Budapest = new(47.5, 19.04, 100);
    private static readonly DateTime Evening = new(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc);       // before dusk

    private static CatalogDso Dso(string id, string kind, double ra, double dec, float mag, float major, float minor = float.NaN, string name = "") =>
        new(id, kind, name, ra, dec, mag, major, minor, 0);

    private TonightService Service(params CatalogDso[] dsos) =>
        new(_h.Node, AtlasCatalog.From([], dsos.ToList())) { UtcNow = () => Evening };

    [Fact]
    public void TheDarkPeriodRunsFromDuskToDawn()
    {
        var (from, to, kind) = TonightService.DarkPeriod(Evening, Budapest)!.Value;
        Assert.Equal("astronomical dark", kind);
        Assert.InRange((to - from).TotalHours, 7, 12);
        Assert.True(from > Evening);                                       // not dark yet at 15:00 UTC
        // in the middle of the night the period starts now
        var (f2, _, _) = TonightService.DarkPeriod(from.AddHours(2), Budapest)!.Value;
        Assert.Equal(from.AddHours(2), f2);
        // where it never gets dark at all (the Arctic in June), nothing
        Assert.Null(TonightService.DarkPeriod(new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc), new GeoSite(80, 20, 0)));
    }

    [Fact]
    public void ObjectsThatAreWellPlacedComeFirstAndOnesThatCannotBeSeenNotAtAll()
    {
        var svc = Service(
            Dso("M 31", "Galaxy", 0.712, 41.27, 3.4f, 190, 60, "Andromeda Galaxy"),      // high all evening
            Dso("M 42", "Nebula", 5.588, -5.39, 4.0f, 85, 60, "Orion Nebula"),           // rises after midnight
            Dso("M 13", "GlobularCluster", 16.695, 36.46, 5.8f, 20, 20),                 // sets in the evening
            Dso("NGC 1976", "Nebula", 5.588, -5.39, 4.0f, 85, 60),                       // M 42 again
            Dso("NGC 1", "Galaxy", 0.12, -70, 8.0f, 5, 3),                               // never up here
            Dso("NGC 2", "Galaxy", 1.0, 40, 13.5f, 5, 3),                                // too faint
            Dso("NGC 3", "Galaxy", 1.0, 40, 8.0f, 0.5f, 0.5f),                           // too small
            Dso("IC 5", "Other", 1.0, 40, 8.0f, 30, 30));                                // not a target kind
        var list = svc.Compute(new TonightRequest { FovWidthDegrees = 1.5, FovHeightDegrees = 1.0, MaxResults = 10 }, Budapest, 15, []);
        Assert.True(list.Ok.Value, list.Message.Text);
        var names = list.Targets.Select(t => t.Label.Text).ToList();
        Assert.Contains("M 31", names); Assert.Contains("M 42", names);
        Assert.DoesNotContain("NGC 1976", names);                                          // once, as the Messier one
        Assert.DoesNotContain(names, n => n is "NGC 1" or "NGC 2" or "NGC 3" or "IC 5");
        var m31 = list.Targets.First(t => t.Label.Text == "M 31"); var m42 = list.Targets.First(t => t.Label.Text == "M 42");
        Assert.True(m31.GoodHours.Value > m42.GoodHours.Value, $"{m31.GoodHours.Value} vs {m42.GoodHours.Value}");
        Assert.True(m31.PeakAltitude.Value > 60);
        Assert.True(m31.Panels.Value >= 2 && m42.Panels.Value == 1);                      // Andromeda is bigger than the frame
        Assert.Contains("good hours", m31.Why.Text);
        Assert.Contains("panels", m31.Why.Text); Assert.Contains("of your frame", m42.Why.Text);
        // best first
        Assert.Equal(list.Targets.OrderByDescending(t => t.Score.Value).Select(t => t.Label.Text), names);
        Assert.InRange(list.Targets.Max(t => t.Score.Value), 20, 100);
    }

    [Fact]
    public void ABigFrameFitsABigObjectAndASmallFrameLikesAPlanetaryNebula()
    {
        var svc = Service(Dso("M 31", "Galaxy", 0.712, 41.27, 3.4f, 190, 60), Dso("M 57", "PlanetaryNebula", 18.893, 33.03, 8.8f, 1.8f, 1.5f));
        TonightTarget Of(TonightList l, string id) => l.Targets.First(t => t.Label.Text == id);
        var wide = svc.Compute(new TonightRequest { FovWidthDegrees = 5, FovHeightDegrees = 3.5 }, Budapest, 15, []);
        var narrow = svc.Compute(new TonightRequest { FovWidthDegrees = 0.3, FovHeightDegrees = 0.2 }, Budapest, 15, []);
        Assert.Equal(1, Of(wide, "M 31").Panels.Value);
        Assert.True(Of(narrow, "M 31").Panels.Value > 5);
        Assert.True(Of(narrow, "M 57").FrameFraction.Value > Of(wide, "M 57").FrameFraction.Value * 10);
    }

    [Fact]
    public void ABrightMoonCountsAgainstAnObjectNextToIt()
    {
        var fullMoon = new DateTime(2026, 10, 26, 15, 0, 0, DateTimeKind.Utc);
        var (from, to, _) = TonightService.DarkPeriod(fullMoon, Budapest)!.Value;
        var mid = from.AddHours((to - from).TotalHours / 2);
        var (mra, mdec) = SolarSystem.Topocentric(SolarSystem.Position(Body.Moon, mid), mid, Budapest);
        // two identical objects: one beside the Moon, one on the other side of the sky
        var near = Dso("NGC 10", "Galaxy", mra, mdec + 8, 8.0f, 20, 15);
        var far = Dso("NGC 20", "Galaxy", Horizon.LocalSiderealHours(mid, Budapest.LongitudeDegrees), 45, 8.0f, 20, 15);   // on the meridian at midnight
        var svc = new TonightService(_h.Node, AtlasCatalog.From([], [near, far])) { UtcNow = () => fullMoon };
        var list = svc.Compute(new TonightRequest { MagnitudeLimit = 12 }, Budapest, 0, []);
        Assert.True(list.MoonIllumination.Value > 0.9, $"{list.MoonIllumination.Value}");
        var n = list.Targets.FirstOrDefault(t => t.Label.Text == "NGC 10");
        if (n is not null) { Assert.True(n.MoonSeparationDegrees.Value < 15); Assert.Contains("Moon", n.Why.Text); }
        var f = list.Targets.First(t => t.Label.Text == "NGC 20");
        Assert.True(n is null || n.Score.Value < f.Score.Value || n.GoodHours.Value < f.GoodHours.Value, "the one beside the Moon should not beat the other for the Moon's sake");
    }

    [Fact]
    public async Task TheHorizonOfTheSiteLimitsWhenAnObjectCounts()
    {
        var svc = Service(Dso("M 31", "Galaxy", 0.712, 41.27, 3.4f, 190, 60));
        var open = svc.Compute(new TonightRequest(), Budapest, 15, []);
        // a wall to the north-east at 60 degrees where Andromeda is in the evening
        var walled = svc.Compute(new TonightRequest(), Budapest, 15, Enumerable.Range(0, 361).Where(a => a is >= 20 and <= 120).Select(a => ((double)a, 70.0)).ToList());
        double Hours(TonightList l) => l.Targets.FirstOrDefault()?.GoodHours.Value ?? 0;
        Assert.True(Hours(walled) < Hours(open), $"{Hours(walled)} vs {Hours(open)}");
        await Task.CompletedTask;
    }

    /// <summary>With the real catalogue (when KStars' data is installed): a sensible list for a real site and a real night.</summary>
    [Fact]
    public void TheRealCatalogueGivesASensibleList()
    {
        if (!Directory.Exists("/usr/share/kstars")) return;
        var catalog = AtlasCatalog.LoadKStars();
        var svc = new TonightService(_h.Node, catalog) { UtcNow = () => Evening };
        var list = svc.Compute(new TonightRequest { FovWidthDegrees = 4.0, FovHeightDegrees = 2.7, MaxResults = 15 }, Budapest, 15, []);
        foreach (var t in list.Targets) output.WriteLine($"{t.Score.Value,5:0}  {t.Label.Text,-10} {t.Kind.Text,-16} {t.CommonName.Text,-24} mag {t.Magnitude.value,4:0.0}  {t.Why.Text}");
        Assert.True(list.Ok.Value, list.Message.Text);
        Assert.InRange(list.Targets.Count, 8, 15);
        Assert.Contains(list.Targets, t => t.Label.Text is "M 31" or "M 33" or "M 27" or "M 57" or "M 13" or "M 15" or "M 2" or "M 45" || t.CommonName.Text.Contains("Nebula"));
        Assert.All(list.Targets, t => Assert.True(t.GoodHours.Value >= 0.5));
    }
}
