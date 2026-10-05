using System.Globalization;
using ELink.Contracts.Atlas;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Atlas;

/// <summary>What to image tonight: the deep-sky objects that are best placed in the dark hours at your site and that suit your frame. For
/// every object of the catalogue: how long it is high enough above your horizon while it is dark (higher counting more), how bright its surface
/// is (what a short night can show), how well it fills your frame (or how many panels it would take), and how far from the Moon it is.</summary>
public sealed class TonightService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly AtlasCatalog _catalog;
    /// <summary>The clock (tests set it).</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private static readonly HashSet<string> DefaultKinds = ["Galaxy", "Nebula", "PlanetaryNebula", "SupernovaRemnant", "OpenCluster", "GlobularCluster"];

    public TonightService(TypeSafeEVentNode node, AtlasCatalog catalog)
    {
        _node = node; _catalog = catalog;
        _commands = new CommandSet(node);
    }

    public Task StartAsync() => _commands.AddAsync<TonightRequest, TonightList>(AtlasIds.Tonight, r => Task.Run(() => ComputeAsync(r)), "the best objects to image in tonight's dark hours");

    private async Task<TonightList> ComputeAsync(TonightRequest r)
    {
        var state = (await _node.CallFunctionAsync<NOTESVoid, SiteState>(SiteIds.GetState(SiteIds.Default), NOTESVoid.Void, TimeSpan.FromSeconds(5)))?.FirstOrDefault();
        if (state is not { Known.Value: true }) return new TonightList { Message = "set your site (Rig › Site) and tonight's best can be worked out" };
        var site = new GeoSite(state.Config.LatitudeDegrees.Value, state.Config.LongitudeDegrees.Value, state.Config.ElevationMeters.Value);
        var profile = state.Config.Horizon.Select(p => (p.AzimuthDegrees.Value, p.AltitudeDegrees.Value)).ToList();
        return Compute(r, site, state.Config.MinAltitudeDegrees.Value, profile);
    }

    private static double SunAlt(DateTime t, GeoSite s)
    {
        var p = SolarSystem.Position(Body.Sun, t);
        return Horizon.AltAzJ2000(p.RaHours, p.DecDegrees, t, s).Altitude;
    }

    /// <summary>The dark period: from now (or dusk) to dawn. Falls back to nautical and civil twilight where it never gets properly dark.</summary>
    public static (DateTime From, DateTime To, string Kind)? DarkPeriod(DateTime now, GeoSite site)
    {
        foreach (var (limit, kind) in new[] { (-18.0, "astronomical dark"), (-12.0, "nautical dark"), (-6.0, "civil dark") })
        {
            if (SunAlt(now, site) < limit)
            {
                var rise = Horizon.Events(t => SunAlt(t, site), limit, now, now.AddHours(30), TimeSpan.FromMinutes(10)).FirstOrDefault(e => e.Kind == Horizon.EventKind.Rise);
                return (now, rise is { Utc.Year: > 1 } ? rise.Utc : now.AddHours(10), kind);
            }
            var events = Horizon.Events(t => SunAlt(t, site), limit, now, now.AddHours(40), TimeSpan.FromMinutes(10));
            var set = events.FirstOrDefault(e => e.Kind == Horizon.EventKind.Set);
            if (set is { Utc.Year: > 1 })
            {
                var dawn = events.FirstOrDefault(e => e.Kind == Horizon.EventKind.Rise && e.Utc > set.Utc);
                return (set.Utc, dawn is { Utc.Year: > 1 } ? dawn.Utc : set.Utc.AddHours(8), kind);
            }
        }
        return null;
    }

    public TonightList Compute(TonightRequest r, GeoSite site, double flatMinimum, IReadOnlyList<(double Az, double Alt)> profile)
    {
        var now = UtcNow();
        if (DarkPeriod(now, site) is not { } night) return new TonightList { Message = "it does not get dark enough here tonight" };
        var list = new TonightList { Ok = true, DarkFromUtc = night.From.ToString("o", CultureInfo.InvariantCulture), DarkToUtc = night.To.ToString("o", CultureInfo.InvariantCulture) };
        double hours = (night.To - night.From).TotalHours;
        if (hours < 0.25) { list.Message = "it is nearly dawn: not enough dark left"; return list; }
        var mid = night.From.AddHours(hours / 2);
        var moon = SolarSystem.Position(Body.Moon, mid);
        var (mra, mdec) = SolarSystem.Topocentric(moon, mid, site);
        list.MoonIllumination = Math.Round(moon.Illumination, 2);
        var kinds = r.Kinds.Text.Trim() == "" ? DefaultKinds : r.Kinds.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        double fovW = r.FovWidthDegrees.Value > 0 ? r.FovWidthDegrees.Value : 1.5, fovH = r.FovHeightDegrees.Value > 0 ? r.FovHeightDegrees.Value : 1.0;
        double minAlt = Math.Clamp(r.MinAltitudeDegrees.Value, 0, 80);
        int steps = Math.Clamp((int)Math.Ceiling(hours * 4), 2, 80);
        var times = Enumerable.Range(0, steps + 1).Select(i => night.From.AddHours(hours * i / steps)).ToArray();
        double stepHours = hours / steps;

        var scored = new List<TonightTarget>();
        foreach (var d in _catalog.AllDsos)
        {
            if (!kinds.Contains(d.Kind) || float.IsNaN(d.Magnitude) || d.Magnitude > r.MagnitudeLimit.Value || !(d.MajorArcmin >= 1.5f)) continue;
            // far from the sky that ever gets high enough here
            if (90 - Math.Abs(site.LatitudeDegrees - d.DecDegrees) < minAlt + 5) continue;
            double good = 0, peak = -90; DateTime peakAt = times[0];
            for (int i = 0; i < times.Length; i++)
            {
                var (alt, az) = Horizon.AltAzJ2000(d.RaHours, d.DecDegrees, times[i], site);
                if (alt > peak) { peak = alt; peakAt = times[i]; }
                double floor = Math.Max(minAlt, Horizon.ProfileAltitude(flatMinimum, profile, az));
                if (alt >= floor) good += Math.Min(1.0, 0.45 + (alt - floor) / 40) * stepHours;      // higher is better: less air, less glow
            }
            if (good < 0.5) continue;
            double major = d.MajorArcmin, minor = float.IsNaN(d.MinorArcmin) || d.MinorArcmin <= 0 ? major * 0.7 : d.MinorArcmin;
            double sb = d.Magnitude + 2.5 * Math.Log10(Math.Max(Math.PI / 4 * major * minor, 0.3));          // magnitudes per square arcminute: lower is brighter
            double fovMin = Math.Min(fovW, fovH) * 60;
            double fraction = major / fovMin;
            double fit = fraction < 0.06 ? 0.15 : fraction < 0.2 ? 0.15 + (fraction - 0.06) / 0.14 * 0.7 : fraction <= 0.95 ? 1.0 : Math.Max(0.35, 1.0 / fraction);
            // how many frames across and down (a little overlap, and a frame that is a hair too small still counts as one)
            int Across(double size, double fov) => Math.Max(1, (int)Math.Ceiling(size / (fov * 60 * 0.92) - 0.1));
            int panels = Math.Min(Across(major, fovW) * Across(minor, fovH), Across(major, fovH) * Across(minor, fovW));
            double sep = Sky.SeparationDegrees(d.RaHours, d.DecDegrees, mra, mdec);
            double moonFactor = 1 - moon.Illumination * (1 - Math.Clamp(sep / 70, 0, 1)) * 0.8;
            double visibility = Math.Min(good, 10) / 10;
            double brightness = Math.Pow(Math.Clamp((22 - sb) / 8.5, 0, 1), 1.3);
            // what makes a picture people are glad of: nebulae and galaxies over star clusters
            double kindFactor = d.Kind switch { "OpenCluster" => 0.8, "GlobularCluster" => 0.9, "PlanetaryNebula" => 0.95, _ => 1.0 };
            double score = 100 * (0.40 * visibility + 0.32 * brightness + 0.28 * fit) * moonFactor * kindFactor;
            var parts = new List<string> { FormattableString.Invariant($"up {good:0.#} good hours, peaks at {peak:0}°") };
            parts.Add(panels > 1 ? FormattableString.Invariant($"{panels} panels to cover it") : FormattableString.Invariant($"fills {Math.Min(fraction, 1) * 100:0}% of your frame"));
            if (moon.Illumination > 0.3) parts.Add(FormattableString.Invariant($"Moon {sep:0}° away"));
            scored.Add(new TonightTarget
            {
                Label = d.Id, Kind = d.Kind, CommonName = FirstName(d.CommonName), RaHours = d.RaHours, DecDegrees = d.DecDegrees, Magnitude = d.Magnitude, MajorArcmin = d.MajorArcmin,
                GoodHours = Math.Round(good, 1), PeakAltitude = Math.Round(peak, 0), PeakUtc = peakAt.ToString("o", CultureInfo.InvariantCulture), MoonSeparationDegrees = Math.Round(sep, 0),
                FrameFraction = Math.Round(fraction, 2), Panels = panels, Score = Math.Round(score, 1), Why = string.Join(", ", parts),
            });
        }
        // the same object under two designations (M 42 and NGC 1976): once, as the Messier one
        var kept = new List<TonightTarget>();
        foreach (var t in scored.OrderByDescending(t => t.Score.Value).ThenBy(t => t.Label.Text.StartsWith("M ") ? 0 : 1))
            if (!kept.Any(k => Sky.SeparationDegrees(k.RaHours.Value, k.DecDegrees.Value, t.RaHours.Value, t.DecDegrees.Value) < 0.05)) kept.Add(t);
        foreach (var t in kept.Take(Math.Clamp(r.MaxResults.Value, 1, 50))) list.Targets.Add(t);
        if (list.Targets.Count == 0) list.Message = "nothing in the catalogue is well placed tonight";
        return list;
    }

    /// <summary>"Andromeda Galaxy, PGC 2557, UGC 454, NGC 224": the first name.</summary>
    private static string FirstName(string names) =>
        names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(n => !System.Text.RegularExpressions.Regex.IsMatch(n, @"^(PGC|UGC|MCG|CGCG|IRAS|2MASX|LEDA|ESO|VV|Arp|NGC|IC|M)\s?\d")) ?? "";

    public ValueTask DisposeAsync() { _commands.Dispose(); return ValueTask.CompletedTask; }
}
