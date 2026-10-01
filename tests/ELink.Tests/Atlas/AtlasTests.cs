using System.Net;
using System.Net.Sockets;
using ELink.Atlas;
using ELink.Contracts.Atlas;
using ELink.Core;
using ELink.Core.Astro;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Atlas;

public class SkyIndexTests
{
    [Fact]
    public void ConeFindsExactlyThePointsInside()
    {
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 20000).Select(_ => (Ra: rnd.NextDouble() * 24, Dec: Math.Asin(rnd.NextDouble() * 2 - 1) * 180 / Math.PI)).ToList();
        var index = new SkyIndex<(double Ra, double Dec)>(pts, p => (p.Ra, p.Dec));
        foreach (var (ra, dec, r) in new[] { (5.5, 0.0, 10.0), (23.9, 10.0, 5.0), (0.05, -30.0, 3.0), (12.0, 88.0, 6.0), (3.0, -89.5, 2.0), (18.0, 45.0, 0.5) })
        {
            var expected = pts.Where(p => Sky.SeparationDegrees(ra, dec, p.Ra, p.Dec) <= r).ToHashSet();
            var found = index.Cone(ra, dec, r).ToHashSet();
            Assert.True(expected.SetEquals(found), $"cone ({ra},{dec},{r}): expected {expected.Count}, found {found.Count}");
        }
    }
}

/// <summary>Against the sky data KStars installs. Skipped (passes trivially) when it is not there.</summary>
public class AtlasCatalogTests
{
    private static readonly Lazy<AtlasCatalog?> Catalog = new(() => File.Exists("/usr/share/kstars/namedstars.dat") ? AtlasCatalog.LoadKStars() : null);

    [Fact]
    public void LoadsStarsDeepSkyObjectsAndConstellations()
    {
        if (Catalog.Value is not { } c) return;
        Assert.True(c.Stars.Count > 40000, $"{c.Stars.Count} stars");
        Assert.True(c.AllDsos.Count > 13000);
        Assert.True(c.Constellations.Segments.Count > 1000);
        Assert.True(c.Constellations.Labels.Count >= 88);
        var sirius = c.NamedStars.Single(s => s.Label == "Sirius");
        Assert.Equal(6.7525, sirius.RaHours, 2); Assert.Equal(-16.716, sirius.DecDegrees, 1); Assert.Equal(-1.44, (double)sirius.Magnitude, 2);
    }

    [Fact]
    public async Task ARegionAroundOrionHoldsItsStarsAndNebula()
    {
        if (Catalog.Value is not { } c) return;
        var svc = new AtlasService(null!, c);
        var chunk = await svc.QueryAsync(new AtlasQuery { RaHours = 5.6, DecDegrees = 0, RadiusDegrees = 12, StarMagnitudeLimit = 6, DsoMagnitudeLimit = 10 });
        Assert.Contains(chunk.Stars, s => s.Label.Text == "Betelgeuse");
        Assert.Contains(chunk.Stars, s => s.Label.Text == "Rigel");
        Assert.All(chunk.Stars, s => Assert.True(s.Magnitude.value <= 6));
        Assert.Contains(chunk.Dsos, d => d.Id.Text == "M 42" && d.Kind.Text == "Nebula");
        Assert.All(chunk.Stars, s => Assert.True(Sky.SeparationDegrees(5.6, 0, s.RaHours.value, s.DecDegrees.value) <= 12 + 1e-3));

        var capped = await svc.QueryAsync(new AtlasQuery { RaHours = 5.6, DecDegrees = 0, RadiusDegrees = 30, StarMagnitudeLimit = 8, MaxStars = 100 });
        Assert.True(capped.Truncated.Value);
        Assert.Equal(100, capped.Stars.Count);
        Assert.True(capped.Stars.Max(s => s.Magnitude.value) < 5, "the brightest are kept");
    }

    [Theory]
    [InlineData("M42", "M 42")] [InlineData("m 31", "M 31")] [InlineData("NGC1976", "M 42")] [InlineData("ngc 224", "M 31")]
    [InlineData("Vega", "Vega")] [InlineData("andromeda gal", "M 31")] [InlineData("Orion Neb", "M 42")]
    public void SearchFindsByDesignationAndName(string text, string first)
    {
        if (Catalog.Value is not { } c) return;
        var hits = c.Search(text);
        Assert.NotEmpty(hits);
        Assert.Equal(first, hits[0].Label);
    }

    [Fact]
    public void SearchForNonsenseFindsNothing()
    {
        if (Catalog.Value is not { } c) return;
        Assert.Empty(c.Search("zzqqxx"));
        Assert.Empty(c.Search(""));
    }

    [Fact]
    public async Task SmallFieldsGetFaintStarsFromTheGsc()
    {
        if (Catalog.Value is not { DeepStars: not null } c) return;
        var svc = new AtlasService(null!, c);
        var bright = await svc.QueryAsync(new AtlasQuery { RaHours = 5.6, DecDegrees = -5.4, RadiusDegrees = 0.5, StarMagnitudeLimit = 8 });
        var deep = await svc.QueryAsync(new AtlasQuery { RaHours = 5.6, DecDegrees = -5.4, RadiusDegrees = 0.5, StarMagnitudeLimit = 13 });
        Assert.True(deep.Stars.Count > bright.Stars.Count + 20, $"bright {bright.Stars.Count}, deep {deep.Stars.Count}");
        Assert.All(deep.Stars, s => Assert.True(Sky.SeparationDegrees(5.6, -5.4, s.RaHours.value, s.DecDegrees.value) <= 0.52));
        Assert.True(deep.Stars.Max(s => s.Magnitude.value) > 11);
    }
}

public class AtlasServiceTests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    [Fact]
    public async Task ServesQueriesSearchAndConstellationsOnTheMesh()
    {
        var stars = new List<CatalogStar> { new(5.92, 7.41, 0.45f, 1.85f, 39801, "Betelgeuse"), new(5.24, -8.2, 0.13f, -0.03f, 34085, "Rigel"), new(18.6, 38.8, 0.03f, 0f, 172167, "Vega") };
        var dsos = new List<CatalogDso> { new("M 42", "Nebula", "Orion Nebula, NGC 1976", 5.588, -5.39, 4f, 90, 60, 0) };
        var figures = new ConstellationFigure(new[] { ((5.92, 7.41), (5.24, -8.2)) }, new[] { ("Ori", "Orion", 5.5, 5.0) });
        var catalog = AtlasCatalog.From(stars, dsos, figures);
        using var node = ElinkNode.Create("AT-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        await using var svc = new AtlasService(node, catalog); await svc.StartAsync();

        var chunk = (await node.CallFunctionAsync<AtlasQuery, AtlasChunk>(AtlasIds.Query, new AtlasQuery { RaHours = 5.6, DecDegrees = 0, RadiusDegrees = 15 }))!.Single();
        Assert.Equal(new[] { "Betelgeuse", "Rigel" }, chunk.Stars.Select(s => s.Label.Text).Order().ToArray());
        Assert.Equal("M 42", chunk.Dsos.Single().Id.Text);

        var hits = (await node.CallFunctionAsync<BinaryConvertibleString, AtlasHits>(AtlasIds.Search, (BinaryConvertibleString)"ngc1976"))!.Single();
        Assert.Equal("M 42", hits.Hits[0].Label.Text);

        var set = (await node.CallFunctionAsync<NOTESVoid, ConstellationSet>(AtlasIds.Constellations, NOTESVoid.Void))!.Single();
        Assert.Single(set.Lines); Assert.Equal("Orion", set.Labels.Single().Label.Text);
    }
}
