using ELink.Contracts.Atlas;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Atlas;

/// <summary>The sky atlas on the mesh: region queries, constellation figures and search, for any UI or planner.</summary>
public sealed class AtlasService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly AtlasCatalog _catalog;
    private readonly CommandSet _commands;
    private ConstellationSet? _figures;

    /// <summary>Fields at most this wide (radius, degrees) also get faint stars from the GSC.</summary>
    public double DeepStarRadiusLimit { get; set; } = 3.0;

    public AtlasService(TypeSafeEVentNode node, AtlasCatalog catalog)
    {
        _node = node; _catalog = catalog;
        _commands = new CommandSet(node);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<AtlasQuery, AtlasChunk>(AtlasIds.Query, QueryAsync, "stars and deep-sky objects in a region of the sky");
        await _commands.AddAsync<NOTESVoid, ConstellationSet>(AtlasIds.Constellations, _ => Task.FromResult(Figures()), "constellation lines and labels of the whole sky");
        await _commands.AddAsync<BinaryConvertibleString, AtlasHits>(AtlasIds.Search, s => Task.FromResult(Search(s.Text)), "find a star or deep-sky object by name or designation");
    }

    public async Task<AtlasChunk> QueryAsync(AtlasQuery q)
    {
        var chunk = new AtlasChunk();
        double ra = q.RaHours.Value, dec = q.DecDegrees.Value, r = Math.Clamp(q.RadiusDegrees.Value, 0.01, 180);
        int maxStars = Math.Clamp(q.MaxStars.Value, 0, 200000), maxDsos = Math.Clamp(q.MaxDsos.Value, 0, 20000);
        double starLimit = q.StarMagnitudeLimit.Value, dsoLimit = q.DsoMagnitudeLimit.Value;

        var stars = _catalog.Stars.Cone(ra, dec, r).Where(s => s.Magnitude <= starLimit).ToList();
        if (_catalog.DeepStars is { } gsc && starLimit > AtlasCatalog.DeepStarsFrom && r <= DeepStarRadiusLimit && stars.Count < maxStars)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                stars.AddRange(await gsc.QueryAsync(ra, dec, r, AtlasCatalog.DeepStarsFrom, starLimit, maxStars - stars.Count, cts.Token));
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { /* no faint stars this time */ }
        }
        if (stars.Count > maxStars) { chunk.Truncated = true; stars = stars.OrderBy(s => s.Magnitude).Take(maxStars).ToList(); }
        foreach (var s in stars)
            chunk.Stars.Add(new AtlasStar { RaHours = (float)s.RaHours, DecDegrees = (float)s.DecDegrees, Magnitude = s.Magnitude, ColorIndex = s.ColorIndex, Label = s.Label });

        // deep-sky objects: by magnitude when they have one; those without are kept when big enough to matter at this scale
        var dsos = _catalog.Dsos.Cone(ra, dec, r + 1)
            .Where(d => float.IsNaN(d.Magnitude) ? d.MajorArcmin >= r * 60 / 200 : d.Magnitude <= dsoLimit).ToList();
        if (dsos.Count > maxDsos) { chunk.Truncated = true; dsos = dsos.OrderBy(d => float.IsNaN(d.Magnitude) ? 30 : d.Magnitude).Take(maxDsos).ToList(); }
        foreach (var d in dsos)
            chunk.Dsos.Add(new AtlasDso
            {
                Id = d.Id, Kind = d.Kind, CommonName = d.CommonName, RaHours = (float)d.RaHours, DecDegrees = (float)d.DecDegrees, Magnitude = d.Magnitude,
                MajorArcmin = d.MajorArcmin, MinorArcmin = d.MinorArcmin, PositionAngle = d.PositionAngle,
            });
        return chunk;
    }

    private ConstellationSet Figures()
    {
        if (_figures is not null) return _figures;
        var set = new ConstellationSet();
        foreach (var (a, b) in _catalog.Constellations.Segments)
            set.Lines.Add(new ConstellationLine { Ra1Hours = (float)a.Ra, Dec1Degrees = (float)a.Dec, Ra2Hours = (float)b.Ra, Dec2Degrees = (float)b.Dec });
        foreach (var (abbr, name, lra, ldec) in _catalog.Constellations.Labels)
            set.Labels.Add(new ConstellationLabel { Abbreviation = abbr, Label = name, RaHours = (float)lra, DecDegrees = (float)ldec });
        return _figures = set;
    }

    public AtlasHits Search(string text)
    {
        var hits = new AtlasHits();
        foreach (var h in _catalog.Search(text))
            hits.Hits.Add(new AtlasHit { Label = h.Label, Kind = h.Kind, Detail = h.Detail, RaHours = h.Ra, DecDegrees = h.Dec, Magnitude = h.Mag, MajorArcmin = h.Size });
        return hits;
    }

    public ValueTask DisposeAsync() { _commands.Dispose(); return ValueTask.CompletedTask; }
}
