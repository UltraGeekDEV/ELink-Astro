using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Fills one requested image: an area of sky to a depth, worked on by any number of smart scopes.
/// <para>One coverage map (exposure seconds per spot) is shared. Each scope has a worker that, whenever its scope is free,
/// asks the plan for the next spot for <i>its</i> frames (sizes from its imaging trains), shoots there with a small random
/// dither, and paints the result. Spots another scope already covered are skipped, so the scopes share the work
/// without ever being kept in step. Everything per scope (guiding, its own dithering, meridian flips, focus) happens inside
/// the scope. A live stack of all their frames is the resulting image.</para></summary>
public sealed partial class ImagingService : IAsyncDisposable
{
    private sealed record ScopeFrames(string ScopeId, FrameSpec[] Frames, double FinestScale, string? Error, List<string> UnknownAngles, Dictionary<string, double> Scales);
    /// <summary>A layer as resolved: its place in the coverage map, the label and filter it goes by, and the shot that feeds it.</summary>
    private sealed record LayerPlan(int Index, ImagingLayer Def, string Label, string Filter, double Exposure)
    {
        public bool Fits(FrameSpec f) => (Def.MinScaleArcsec.Value <= 0 || f.Scale >= Def.MinScaleArcsec.Value * (1 - 1e-6))
                                         && (Def.MaxScaleArcsec.Value <= 0 || f.Scale > 0 && f.Scale <= Def.MaxScaleArcsec.Value * (1 + 1e-6));
    }
    private sealed record Plan(CoverageMap Map, CoverageMap Actual, List<ScopeFrames> Scopes, double Width, double Height, double Stepover, double Scale, List<LayerPlan> Layers)
    {
        /// <summary>Why the image is coarser than the frames, when the station chose that (said in the preview and the state).</summary>
        public string ScaleNote { get; init; } = "";
        /// <summary>Colour frames are stacked as 2x2 super pixels: for the scale, or because a full debayer does not fit in memory.</summary>
        public bool SuperPixel { get; init; }
        /// <summary>The image carries on a kept one (and so does its live stack).</summary>
        public bool CarriedOn { get; init; }
        /// <summary>How far (degrees, each side) the frames at the area's edges reach beyond it: the sky gathered around it.</summary>
        public double OverX { get; init; }
        public double OverY { get; init; }
        /// <summary>The coverage of everything the frames reached, the area in its middle (shown; the planning uses only the area's).</summary>
        public CoverageMap? Gathered { get; init; }
    }

    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<ImagingState> _publisher;
    private readonly object _gate = new();
    private readonly Random _random = new();
    private ImagingState _state = new();
    private Plan? _plan;
    private readonly Dictionary<string, ImagingWorker> _workers = new();
    private double _target;
    private bool _paused;
    private int _visitsStarted;
    private CancellationTokenSource? _cts;
    private Task? _run;

    private readonly string? _dataDir;
    private string? _runDir;

    /// <param name="dataDir">where started images are kept, to be carried on another night; null = not kept</param>
    public ImagingService(TypeSafeEVentNode node, string? dataDir = null)
    {
        _node = node; _dataDir = dataDir;
        _commands = new CommandSet(node);
        _publisher = new(node, ImagingIds.State, ImagingIds.GetState, BuildState);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<ImagingRequest, CommandResult>(ImagingIds.Start, async r =>
        {
            try { return await StartRunAsync(r); }
            catch (Exception ex) { return CommandResult.Fail("could not start: " + ex.Message); }
        }, "fill an area of sky to a depth with any number of scopes");
        await _commands.AddAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, async r =>
        {
            try { return await PreviewAsync(r); }
            catch (Exception ex) { return new ImagingState { Phase = "Error", Message = ex.Message }; }
        }, "resolve a request (frames per scope, area) without running it");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ImagingIds.Pause, _ => SetPaused(true), "every scope stops after its current shot");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ImagingIds.Resume, _ => SetPaused(false), "carry on");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ImagingIds.Abort, _ => AbortAsync(), "stop now");
        await _commands.AddAsync<BinaryConvertibleDouble, CommandResult>(ImagingIds.SetTarget, SetTargetAsync, "change the depth while running");
        await _commands.AddAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, _ => Task.FromResult(ListSaved()), "images that were started and can be carried on");
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(ImagingIds.DeleteSaved, l => Task.FromResult(DeleteSaved(l.Text)), "forget a kept image");
        await _publisher.StartAsync();
        StartResuming();
    }

    // ---- resolving a request ------------------------------------------------------------------------------------

    private async Task<CompositionSnapshot> SnapshotAsync()
    {
        var merged = new CompositionSnapshot();
        var snaps = await _node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void, TimeSpan.FromSeconds(10));
        foreach (var s in snaps ?? new())
        {
            foreach (var x in s.Scopes) merged.Scopes.Add(x);
            foreach (var x in s.Trains) merged.Trains.Add(x);
            foreach (var x in s.CameraShooters) merged.CameraShooters.Add(x);
            foreach (var x in s.MountPointers) merged.MountPointers.Add(x);
        }
        return merged;
    }

    /// <summary>The frames a scope shoots on every visit: each imaging camera of each of its trains (nested scopes
    /// included), placed by the shooter offsets relative to its primary shooter, which is what the scope centres.</summary>
    private async Task<ScopeFrames> FramesOfAsync(string scopeId, CompositionSnapshot snap)
    {
        var frames = new List<FrameSpec>();
        var unknownAngles = new List<string>();
        var scales = new Dictionary<string, double>();      // arcsec per pixel of each camera whose angle is unknown
        double finest = double.NaN;
        string? error = await Collect(scopeId, 0, 0, 0);
        if (error is null && frames.Count == 0) error = $"scope {scopeId} has no imaging camera with a known field";
        return new ScopeFrames(scopeId, frames.ToArray(), finest, error, unknownAngles, scales);

        async Task<string?> Collect(string id, double dE, double dN, int depth)
        {
            if (depth > 4) return "scopes nested too deep";
            var def = snap.Scopes.FirstOrDefault(s => s.Id.Text == id);
            if (def is null) return $"no scope {id}";
            if (def.Shooters.Count == 0) return $"scope {id} has no shooter";
            double pe = def.Shooters[0].OffsetEastArcmin.Value / 60, pn = def.Shooters[0].OffsetNorthArcmin.Value / 60;
            foreach (var sh in def.Shooters)
            {
                double oe = dE + sh.OffsetEastArcmin.Value / 60 - pe, on = dN + sh.OffsetNorthArcmin.Value / 60 - pn;
                string sid = sh.Id.Text;
                if (snap.Trains.Any(t => t.Id.Text == sid))
                {
                    var states = await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(sid), NOTESVoid.Void, TimeSpan.FromSeconds(10));
                    if (states?.FirstOrDefault() is not { } t) return $"train {sid} does not answer";
                    foreach (var c in t.Cameras.Where(c => c.Role.Text == "Imaging"))
                    {
                        if (!(c.FieldWidthDegrees.Value > 0) || !(c.FieldHeightDegrees.Value > 0))
                            return $"train {sid}: the field of {c.CameraId.Text} is not known (focal length set? camera connected?)";
                        // the camera's angle on the sky, if a solve has told its train (no rotator: it stays put)
                        double angle = double.IsNaN(c.AngleDegrees.Value) ? 0 : c.AngleDegrees.Value;
                        if (double.IsNaN(c.AngleDegrees.Value)) { unknownAngles.Add(c.ShooterId.Text); scales[c.ShooterId.Text] = c.PixelScaleArcsec.Value; }
                        frames.Add(new FrameSpec(c.FieldWidthDegrees.Value / 2, c.FieldHeightDegrees.Value / 2, angle, oe, on, 1, double.IsNaN(c.PixelScaleArcsec.Value) ? 0 : c.PixelScaleArcsec.Value));
                        if (double.IsNaN(finest) || c.PixelScaleArcsec.Value < finest) finest = c.PixelScaleArcsec.Value;
                    }
                }
                else if (snap.Scopes.Any(s => s.Id.Text == sid))
                {
                    if (await Collect(sid, oe, on, depth + 1) is { } e) return e;
                }
                else return $"{sid} has no known field: put its camera in an imaging train";
            }
            return null;
        }
    }

    private static string? Validate(ImagingRequest r)
    {
        if (r.ScopeIds.Count == 0) return "name at least one scope";
        if (r.Center.Epoch.Text != "J2000") return "the centre must be J2000";
        double ra = r.Center.RaHours.Value, dec = r.Center.DecDegrees.Value;
        if (double.IsNaN(ra) || ra < 0 || ra >= 24 || double.IsNaN(dec) || Math.Abs(dec) > 90) return "the centre is not on the sky";
        if (r.WidthDegrees.Value < 0 || r.HeightDegrees.Value < 0 || r.WidthDegrees.Value > 60 || r.HeightDegrees.Value > 60) return "the area must be 0..60 degrees on a side";
        if (!(r.Exposure.Seconds.Value > 0)) return "the exposure must be longer than zero";
        if (r.TargetSeconds.Value < 0 || r.StepoverDegrees.Value < 0 || r.DitherArcsec.Value < 0 || r.MaxVisits.Value < 0) return "target, stepover, dither and visit limit cannot be negative";
        if (r.Path.Text is { Length: > 0 } path && !string.Equals(path, "Rows", StringComparison.OrdinalIgnoreCase) && !string.Equals(path, "Hilbert", StringComparison.OrdinalIgnoreCase)) return $"unknown path \"{path}\" (Rows or Hilbert)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in r.Layers)
        {
            if (l.Label.Text.Trim() == "") return "every layer needs a name";
            if (!names.Add(l.Label.Text.Trim())) return $"two layers are called '{l.Label.Text}'";
            if (l.MinScaleArcsec.Value < 0 || l.MaxScaleArcsec.Value < 0 || l.TargetSeconds.Value < 0 || l.ExposureSeconds.Value < 0
                || double.IsNaN(l.MinScaleArcsec.Value) || double.IsNaN(l.MaxScaleArcsec.Value)) return $"layer '{l.Label.Text}': scales, depth and exposure cannot be negative";
            if (l.MinScaleArcsec.Value > 0 && l.MaxScaleArcsec.Value > 0 && l.MinScaleArcsec.Value > l.MaxScaleArcsec.Value)
                return $"layer '{l.Label.Text}': its finest scale ({l.MinScaleArcsec.Value:0.##}\"/px) is coarser than its coarsest ({l.MaxScaleArcsec.Value:0.##}\"/px)";
        }
        return null;
    }

    private async Task<(Plan? Plan, string? Error)> ResolveAsync(ImagingRequest r, Kept? kept = null)
    {
        if (Validate(r) is { } bad) return (null, bad);
        var snap = await SnapshotAsync();
        var scopes = new List<ScopeFrames>();
        foreach (var id in r.ScopeIds.Select(s => s.Text).Distinct())
        {
            var f = await FramesOfAsync(id, snap);
            if (f.Error is not null) return (null, f.Error);
            scopes.Add(f);
        }
        var layers = r.Layers.Count == 0
            ? new List<LayerPlan> { new(0, new ImagingLayer { Filter = r.Exposure.Filter.Text }, "", r.Exposure.Filter.Text, r.Exposure.Seconds.Value) }
            : r.Layers.Select((l, i) => new LayerPlan(i, l, l.Label.Text.Trim(), l.Filter.Text != "" ? l.Filter.Text : r.Exposure.Filter.Text, l.ExposureSeconds.Value > 0 ? l.ExposureSeconds.Value : r.Exposure.Seconds.Value)).ToList();
        foreach (var l in layers)
            if (!scopes.Any(sc => sc.Frames.Any(l.Fits)))
            {
                string have = string.Join(", ", scopes.SelectMany(sc => sc.Frames).Select(f => f.Scale).Where(x => x > 0).Distinct().Order().Select(x => x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\"/px"));
                return (null, $"layer '{l.Label}': none of the cameras of {string.Join(", ", scopes.Select(sc => sc.ScopeId))} takes frames in its scale range ({(have == "" ? "no known scales" : "they have " + have)})");
            }
        var all = scopes.SelectMany(s => s.Frames).ToList();
        double minFrame = all.Min(f => 2 * Math.Min(f.HalfWidth, f.HalfHeight));
        var smallest = all.OrderBy(f => f.HalfWidth * f.HalfHeight).First();
        // a target with no size is one frame of the smallest train as it is turned on the sky, a little smaller so dithered shots
        // still cover all of it (a camera at 90° stands its frame on end: the frame's unturned size left corners no shot reached)
        var (upW, upH) = InscribedUpright(smallest, smallest.Rotation - r.PositionAngleDegrees.Value);
        double w = r.WidthDegrees.Value > 0 ? r.WidthDegrees.Value : upW * 0.9;
        double h = r.HeightDegrees.Value > 0 ? r.HeightDegrees.Value : upH * 0.9;
        if (Math.Abs(r.Center.DecDegrees.Value) + Math.Max(w, h) / 2 + 2 * all.Max(f => Math.Max(f.HalfWidth, f.HalfHeight)) > 89.5)
            return (null, "the area reaches a celestial pole, where the tiling is undefined");
        double stepover = r.StepoverDegrees.Value > 0 ? r.StepoverDegrees.Value : 0.1 * minFrame;
        double cell = Math.Max(minFrame / 12, Math.Sqrt(w * h / 40000.0));
        var map = new CoverageMap(w, h, cell, layers.Count);
        if (kept is not null)
        {
            if (kept.Layers != layers.Count) return (null, $"the kept image '{r.Label.Text}' has {kept.Layers} layer(s), the request {layers.Count}: start it afresh or use another name");
            // carrying on: the same area, and the coverage it already has, on the grid it was kept on
            if (Math.Abs(kept.Width - w) > 1e-9 || Math.Abs(kept.Height - h) > 1e-9) return (null, $"the kept image '{r.Label.Text}' covers another area ({kept.Width:0.###}° x {kept.Height:0.###}°): start it afresh or use another name");
            map = CoverageMap.Restore(w, h, kept.Cols, kept.Rows, kept.Seconds, layers.Count);
        }
        // the frames at the area's edges hang over it (that is how its edges get their depth): all the sky they reach is gathered,
        // shown with the area outlined in it, and stacked. A frame touching the area is at most its own extent (as it is turned)
        // beyond the edge, and reaches as far again. A kept image goes on with the field its stack was started on.
        double overX = 0, overY = 0, pa = r.PositionAngleDegrees.Value, dither = Math.Max(0, r.DitherArcsec.Value) / 3600;
        foreach (var f in all)
        {
            double t = (f.Rotation - pa) * Math.PI / 180, c = Math.Abs(Math.Cos(t)), s = Math.Abs(Math.Sin(t)), off = Math.Abs(f.OffsetEast) + Math.Abs(f.OffsetNorth);
            overX = Math.Max(overX, 2 * (f.HalfWidth * c + f.HalfHeight * s) + off + dither);
            overY = Math.Max(overY, 2 * (f.HalfWidth * s + f.HalfHeight * c) + off + dither);
        }
        // a tangent grid cannot reach far round the sphere: a wide-angle lens on a wide area gathers no more than MaxGatheredDegrees
        overX = Math.Min(overX, Math.Max(0, (MaxGatheredDegrees - w) / 2)); overY = Math.Min(overY, Math.Max(0, (MaxGatheredDegrees - h) / 2));
        if (kept is not null) (overX, overY) = (Math.Max(0, kept.OverX), Math.Max(0, kept.OverY));
        double sw = w + 2 * overX, sh = h + 2 * overY;      // the gathered field: what the live stack holds
        double scale = r.OutputPixelScaleArcsec.Value > 0 ? r.OutputPixelScaleArcsec.Value : scopes.Where(s => !double.IsNaN(s.FinestScale)).Select(s => s.FinestScale).DefaultIfEmpty(0).Min();
        string note = "";
        bool superPixel = false;
        if (r.LiveStack.Value && scale > 0)
        {
            // no scale asked for: the live stack is the night's picture, not the final image (every sub is kept on disk at full
            // resolution, for a stacker on a proper computer), so it is light: at least twice the frames' scale, where colour frames
            // are read straight into 2x2 super pixels (the cheapest way through the stacker), and no more than a few megapixels
            if (r.OutputPixelScaleArcsec.Value <= 0 && DefaultStackMegapixels > 0)
            {
                double frames = scale;
                long preview = (long)(DefaultStackMegapixels * 1e6) * StackBytesPerPixel * Math.Max(1, layers.Count);
                scale = Math.Max(2 * frames, StackBytes(sw, sh, scale, layers.Count) > preview ? CoarserToFit(scale, StackBytes(sw, sh, scale, layers.Count), preview) : scale);
                note = FormattableString.Invariant($" (the live picture; the subs are kept at the frames' {frames:0.##}\"/px)");
            }
            // the stacks (one per layer) and the frame being stacked must fit in memory. A colour frame debayered at full size is
            // ~16 bytes a sensor pixel (~400 MB for 24 MP): beside the rest a 2 GB Pi could not hold it, and every frame failed to
            // stack while the image went on as if all was well. Read in 2x2 super pixels it is a quarter of that.
            double finest = scopes.Select(s => s.FinestScale).Where(x => x > 0).DefaultIfEmpty(0).Min();
            long budget = ImageBudgetBytes, full = FrameBytes(scopes, 16), packed = FrameBytes(scopes, 4);
            superPixel = finest > 0 && scale >= 2 * finest;           // the stacker reads frames so anyway at that scale
            long stack = StackBytes(sw, sh, scale, layers.Count);
            if (stack + (superPixel ? packed : full) > budget)
            {
                if (!superPixel && stack + packed <= budget)
                {
                    superPixel = true;
                    note += FormattableString.Invariant($" (colour read in 2x2 super pixels: a full debayer of the frames needs {full >> 20} MB, more than this station has)");
                }
                else
                {
                    long room = budget - packed;
                    double fits = room > 0 ? CoarserToFit(scale, stack, room) : double.NaN;
                    if (!(fits > 0))
                        return (null, FormattableString.Invariant($"at {scale:0.##}\"/px the image needs {(stack + packed) >> 20} MB for its stack and frames, more than the {budget >> 20} MB this station has for it, and no coarser scale fits: use a smaller area"));
                    // a scale that was asked for and does not fit: the finest that does, said so (the subs are kept at full
                    // resolution either way; refusing to start left the night unused for a number on the live picture)
                    note = r.OutputPixelScaleArcsec.Value > 0
                        ? FormattableString.Invariant($" (asked {scale:0.##}\"/px, which needs {(stack + packed) >> 20} MB for the stack and frames, more than the {budget >> 20} MB this station has: the finest that fits; the subs are kept at full resolution)")
                        : FormattableString.Invariant($" (the frames' {scale:0.##}\"/px would need {(stack + packed) >> 20} MB for the stack and frames, more than the {budget >> 20} MB this station has)");
                    scale = fits; superPixel = true;
                }
            }
        }
        // the gathered coverage: kept with the image, or (an image kept before there was one) just the area's
        return (new Plan(map, map.Clone(), scopes, w, h, stepover, scale, layers) { ScaleNote = note, SuperPixel = superPixel, CarriedOn = kept is not null, OverX = overX, OverY = overY, Gathered = kept?.Gathered is { } kg ? CoverageMap.Restore(sw, sh, kept.GatheredCols, kept.GatheredRows, kg, layers.Count)
            : kept is not null && overX <= 0 && overY <= 0 ? map.Clone() : new CoverageMap(sw, sh, cell, layers.Count) }, null);
    }

    /// <summary>The widest the gathered field (the area and the sky its edge frames reach around it) gets, in degrees a side.</summary>
    public double MaxGatheredDegrees { get; set; } = 90;

    /// <summary>Bytes a live stack takes per pixel: three colour sums, a weight and a sum of squares, as floats.</summary>
    internal const int StackBytesPerPixel = 20;

    /// <summary>The bytes the largest frame of these scopes takes while it is stacked, at so many bytes a sensor pixel.</summary>
    private static long FrameBytes(List<ScopeFrames> scopes, int perPixel) =>
        (long)scopes.SelectMany(s => s.Frames).Where(f => f.Scale > 0).Select(f => 2 * f.HalfWidth * 3600 / f.Scale * (2 * f.HalfHeight * 3600 / f.Scale)).DefaultIfEmpty(0).Max() * perPixel;

    /// <summary>The share of the memory this process may use (the runtime's limit: a cgroup's, or the machine's) that an image's
    /// stacks and the frame being stacked may take; the rest is for the frames on their way, grading, solving, saving.</summary>
    public double ImageMemoryFraction { get; set; } = 0.5;

    /// <summary>The most megapixels a layer of the live stack has when a request names no scale (0 = the frames' own scale, if it fits).</summary>
    public double DefaultStackMegapixels { get; set; } = 4;

    /// <summary>The scale, rounded up to 0.01"/px, at which a stack of <paramref name="need"/> bytes at <paramref name="scale"/> fits in <paramref name="budget"/>.</summary>
    private static double CoarserToFit(double scale, long need, long budget) => Math.Ceiling(scale * Math.Sqrt((double)need / budget) * 100) / 100;

    /// <summary>For tests: the memory the stacks and the frame being stacked may take, instead of a share of what the runtime may use.</summary>
    public long? ImageBudgetOverride { get; set; }

    private long ImageBudgetBytes => ImageBudgetOverride ?? (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * ImageMemoryFraction);

    private static long StackBytes(double width, double height, double scale, int stacks) =>
        (long)(PlanProjection.TangentSpan(width) * 3600 / scale * (PlanProjection.TangentSpan(height) * 3600 / scale)) * StackBytesPerPixel * Math.Max(1, stacks);

    private async Task<ImagingState> PreviewAsync(ImagingRequest r)
    {
        var (plan, error) = await ResolveAsync(r);
        var s = new ImagingState { Phase = error is null ? "Idle" : "Error", Message = error ?? "", Label = r.Label.Text, TargetSeconds = r.TargetSeconds.Value };
        if (plan is null) return s;
        s.WidthDegrees = plan.Width; s.HeightDegrees = plan.Height;
        foreach (var sc in plan.Scopes) s.Workers.Add(Worker(sc));
        s.Message = FormattableString.Invariant($"{plan.Width:0.###}° x {plan.Height:0.###}°, stepover {plan.Stepover:0.####}°") + (plan.Scale > 0 ? FormattableString.Invariant($", image at {plan.Scale:0.##}\"/px") + plan.ScaleNote : "")
            + (plan.Layers.Count > 1 ? $", {plan.Layers.Count} layers" : "") + SubsPerHour(plan) + HowLong(r, plan);
        return s;
    }

    /// <summary>Seconds a shot takes beyond its exposure (download, the hop to the next spot, settling), for the estimates.</summary>
    public static double ShotOverheadSeconds { get; set; } = 6;

    /// <summary>"; about 1926 subs, 19.3 h of shooting": the planner run dry on a copy of the map (with what a carried-on image has
    /// already), as the image would run it. A mosaic's depth is everywhere, so its time grows with its area, and the frames that
    /// reach over its edges are part of it: this is the number to plan the nights by. Capped in time: a huge plan says "over".</summary>
    private string HowLong(ImagingRequest r, Plan plan)
    {
        bool endless = plan.Layers.Any(l => TargetOf(l, r.TargetSeconds.Value) <= 0);
        if (endless) return "; it runs until stopped";
        var dry = plan with { Map = plan.Map.Clone() };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(3);
        double longest = 0; int subs = 0; bool cut = false;
        foreach (var sc in plan.Scopes)
        {
            var planners = PlannersOf(r, dry, sc, r.TargetSeconds.Value);
            foreach (var (_, p) in planners) if (!p.TopUpOnly) p.Plan();
            double seconds = 0;
            var live = planners.ToList();
            while (live.Count > 0)
            {
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (live[i].Planner.Next() is null) { live.RemoveAt(i); continue; }
                    seconds += live[i].Layer.Exposure + ShotOverheadSeconds; subs++;
                }
                if (subs >= 20000 || clock.Elapsed > budget) { cut = true; break; }
            }
            longest = Math.Max(longest, seconds);
            if (cut) break;
        }
        if (subs == 0) return "; nothing left to take";
        double hours = longest / 3600;
        string per = FormattableString.Invariant($"{plan.Layers.Min(l => l.Exposure) + ShotOverheadSeconds:0.#} s a sub");
        return cut ? FormattableString.Invariant($"; over {subs} subs and {hours:0.#} h of shooting ({per})")
                   : FormattableString.Invariant($"; about {subs} subs, {hours:0.#} h of shooting ({per})");
    }

    /// <summary>"; about 5.5 GB of subs an hour": what the kept frames cost on disk, from the cameras' pixels (16 bits), shots back
    /// to back with a few seconds between them, and lossless compression where the station has it.</summary>
    private static string SubsPerHour(Plan plan)
    {
        double bytes = 0;
        foreach (var sc in plan.Scopes)
        {
            double pixels = sc.Frames.Where(f => f.Scale > 0).Sum(f => 2 * f.HalfWidth * 3600 / f.Scale * (2 * f.HalfHeight * 3600 / f.Scale));
            double seconds = plan.Layers.Min(l => l.Exposure) + 6;
            bytes += pixels * 2 * 3600 / seconds;
        }
        if (bytes <= 0) return "";
        bool packed = ELink.Imaging.FitsCompressor.Available;
        return FormattableString.Invariant($"; about {bytes * (packed ? 0.6 : 1) / 1e9:0.#} GB of subs an hour{(packed ? " (compressed)" : "")}");
    }

    private static ImagingWorker Worker(ScopeFrames sc)
    {
        var w = new ImagingWorker { ScopeId = sc.ScopeId, Phase = "Waiting" };
        foreach (var f in sc.Frames)
            w.Frames.Add(new FrameFootprint { WidthDegrees = 2 * f.HalfWidth, HeightDegrees = 2 * f.HalfHeight, RotationDegrees = f.Rotation, OffsetEastDegrees = f.OffsetEast, OffsetNorthDegrees = f.OffsetNorth });
        return w;
    }

    // ---- running --------------------------------------------------------------------------------------------------

    private async Task<CommandResult> StartRunAsync(ImagingRequest r)
    {
        lock (_gate) if (_cts is not null) return CommandResult.Fail("an image is already being taken: abort it first");
        await LearnAnglesAsync(r);
        // an image of this name started before: carry on with it (same field only), or start it afresh
        string? runDir = _dataDir is null ? null : Path.Combine(_dataDir, "images", Safe(r.Label.Text));
        Kept? kept = null;
        if (runDir is not null && Directory.Exists(runDir))
        {
            if (!r.Resume.Value) Directory.Delete(runDir, true);
            else
            {
                kept = LoadKept(runDir);
                if (kept is not null && Sky.SeparationDegrees(kept.Request.Center.RaHours.Value, kept.Request.Center.DecDegrees.Value, r.Center.RaHours.Value, r.Center.DecDegrees.Value) * 3600 > 1
                    || kept is not null && Math.Abs(kept.Request.PositionAngleDegrees.Value - r.PositionAngleDegrees.Value) > 1e-6)
                    return CommandResult.Fail($"the kept image '{r.Label.Text}' is of another part of the sky: start it afresh or use another name");
            }
        }
        var (plan, error) = await ResolveAsync(r, kept);
        if (plan is null) return CommandResult.Fail(error!);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null) return CommandResult.Fail("an image is already being taken");
            cts = _cts = new CancellationTokenSource();
            _stoppedByPerson = false;
            _backlash.Clear();
            _plan = plan; _target = r.TargetSeconds.Value; _paused = false; _visitsStarted = 0;
            _workers.Clear();
            foreach (var sc in plan.Scopes) _workers[sc.ScopeId] = Worker(sc);
            _state = new ImagingState { Phase = "Running", Label = r.Label.Text, WidthDegrees = plan.Width, HeightDegrees = plan.Height, Visits = kept?.Visits ?? 0 };
            if (kept is not null) _state.Message = $"carrying on: {kept.Visits} shots kept from before";
            else if (plan.ScaleNote != "") _state.Message = FormattableString.Invariant($"image at {plan.Scale:0.##}\"/px") + plan.ScaleNote;
            _runDir = runDir;
        }
        await _publisher.PublishAsync();
        var copy = Copy(r);
        Keep(copy, plan);
        MarkRunning(copy);
        _run = Task.Run(() => RunAsync(copy, plan, cts));
        return CommandResult.Success();
    }

    private async Task StartStackAsync(ImagingRequest r, Plan plan, string? runDir)
    {
        if (!r.LiveStack.Value) return;
        {
            var stack = new LiveStackRequest
            {
                Label = r.Label.Text, Center = new SkyTarget { RaHours = r.Center.RaHours.Value, DecDegrees = r.Center.DecDegrees.Value, Epoch = "J2000" },
                // the stack is a tangent grid: it must be as wide as the area's edges are on the tangent plane (see PlanProjection)
                FovWidthDegrees = PlanProjection.TangentSpan(plan.Width + 2 * plan.OverX), FovHeightDegrees = PlanProjection.TangentSpan(plan.Height + 2 * plan.OverY), PositionAngleDegrees = r.PositionAngleDegrees.Value, PixelScaleArcsec = plan.Scale,
                // with one kind of frame, its scale lets frames be placed by their pointing when they cannot be solved
                FramePixelScaleArcsec = plan.Scopes.Select(s => s.FinestScale).Distinct().Count() == 1 && plan.Scopes[0].FinestScale > 0 ? plan.Scopes[0].FinestScale : 0,
                // only an image that carries on carries its stack on: a stack left from an image that was deleted (or started afresh)
                // under the same name is not this one, and refused to start on its other field
                SessionKey = runDir is null ? "" : r.Label.Text, Resume = r.Resume.Value && plan.CarriedOn,
                // the plan already fits the stacks in this station's memory (ResolveAsync); a big machine may hold more than the default
                MaxMegapixels = Math.Max(40, ImageBudgetBytes / (double)StackBytesPerPixel / 1e6),
            };
            // an image at least twice as coarse as the frames gains nothing from interpolating the colours: each 2x2 cell is one
            // colour pixel (a quarter of the memory and work, which a Raspberry Pi needs)
            double finest = plan.Scopes.Select(s => s.FinestScale).Where(x => x > 0).DefaultIfEmpty(0).Min();
            if (plan.SuperPixel || finest > 0 && plan.Scale >= 2 * finest) stack.Debayer = "SuperPixel";
            foreach (var sc in plan.Scopes) stack.ShooterIds.Add(sc.ScopeId);
            if (r.Layers.Count > 0)
                foreach (var l in plan.Layers) stack.Layers.Add(new ImagingLayer { Label = l.Label, Filter = l.Filter, MinScaleArcsec = l.Def.MinScaleArcsec.Value, MaxScaleArcsec = l.Def.MaxScaleArcsec.Value });
            var started = await Commands.CallAsync(_node, LiveStackIds.Start, stack);
            if (!started.Ok.Value) await Set(s => s.Message = "no live stack: " + started.Error.Text);
        }
    }

    private async Task RunAsync(ImagingRequest r, Plan plan, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        RemoteState<WeatherState>? weather = r.WeatherId.Text == "" ? null
            : new RemoteState<WeatherState>(_node, EquipmentIds.State(DeviceKinds.Weather, r.WeatherId.Text), EquipmentIds.GetState(DeviceKinds.Weather, r.WeatherId.Text));
        string endPhase = "Done", endMessage = "";
        try
        {
            if (weather is not null) await weather.StartAsync();
            // every shot is kept on disk, whoever asked for saving before (a restart forgets it)
            await KeepFramesAsync(plan);
            // the mounts' slack first (its shots are no part of the image), then the stack
            await TrainBacklashAsync(plan, ct);
            await StartStackAsync(r, plan, _runDir);
            // one planner per scope and layer it can feed (its own frames), all painting the same map: a spot one scope covered is skipped by the others
            var planners = plan.Scopes.ToDictionary(s => s.ScopeId, s => PlannersOf(r, plan, s));
            await Task.WhenAll(plan.Scopes.Select(s => Task.Run(() => WorkAsync(r, plan, s, planners[s.ScopeId], weather, ct))));
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var ws = _workers.Values.ToList();
                if (ws.All(w => w.Phase.Text == "Failed")) { endPhase = "Error"; endMessage = string.Join("; ", ws.Select(w => $"{w.ScopeId.Text}: {w.Message.Text}")); }
                else if (ws.Any(w => w.Phase.Text == "Failed")) endMessage = "done, but " + string.Join("; ", ws.Where(w => w.Phase.Text == "Failed").Select(w => $"{w.ScopeId.Text} failed: {w.Message.Text}"));
            }
            // shots were taken but the stack (the image itself) has none of them: not done, whatever the map says
            if (endPhase == "Done" && r.LiveStack.Value && await StackProblemAsync() is { } stackProblem)
            {
                endMessage = endMessage == "" ? stackProblem : endMessage + "; " + stackProblem;
                SessionLog.Error(_node, "imaging", $"'{r.Label.Text}': {stackProblem}");
            }
        }
        catch (OperationCanceledException)
        {
            endPhase = "Aborted"; endMessage = "aborted";
            foreach (var s in plan.Scopes) await Commands.CallAsync(_node, ScopeIds.Command(s.ScopeId, "Abort"), NOTESVoid.Void);
        }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            weather?.Dispose();
            lock (_gate) { _cts = null; _paused = false; }
            cts.Dispose();
            Ended(endPhase);
            // the scopes stop with the image: none is left showing a shot it no longer takes
            lock (_gate) foreach (var w in _workers.Values) if (w.Phase.Text is not ("Done" or "Failed")) { w.Phase = endPhase == "Done" ? "Done" : "Stopped"; w.Message = ""; }
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; });
        }
    }

    /// <summary>What is wrong with the live stack at the end of an image, or null: no frames in it although frames came, or most
    /// of them failed.</summary>
    private async Task<string?> StackProblemAsync()
    {
        try
        {
            var answers = await _node.CallFunctionAsync<NOTESVoid, LiveStackState>(LiveStackIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(10));
            if (answers?.FirstOrDefault() is not { } s) return null;
            int stacked = s.FramesStacked.Value, rejected = s.FramesRejected.Value;
            if (stacked == 0 && rejected > 0) return $"the live stack holds none of the frames ({rejected} rejected: {s.Message.Text})";
            if (rejected > 2 * stacked) return $"the live stack rejected most frames ({rejected} rejected, {stacked} stacked: {s.Message.Text})";
            return null;
        }
        catch (Exception) { return null; }
    }

    private double TargetOf(LayerPlan l) => TargetOf(l, _target);
    /// <param name="depth">the image's depth (the running one's, or a previewed request's)</param>
    private static double TargetOf(LayerPlan l, double depth) => l.Def.TargetSeconds.Value > 0 ? l.Def.TargetSeconds.Value : depth;

    /// <summary>A scope's planners, one for each layer one of its cameras feeds. A shot goes through one filter: the frames that
    /// feed a layer are the planner's, and every camera that feeds any layer of the same filter is painted (it took the picture too).</summary>
    private List<(LayerPlan Layer, CoveragePlanner Planner)> PlannersOf(ImagingRequest r, Plan plan, ScopeFrames s, double? depth = null)
    {
        var list = new List<(LayerPlan, CoveragePlanner)>();
        foreach (var l in plan.Layers)
        {
            var group = plan.Layers.Where(x => string.Equals(x.Filter, l.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
            FrameSpec[] Masked() => s.Frames.Select(f => f with { Mask = group.Where(g => g.Fits(f)).Aggregate(0, (m, g) => m | 1 << g.Index) }).ToArray();
            var masked = Masked();
            var mine = masked.Where(f => (f.Mask >> l.Index & 1) != 0).ToArray();
            if (mine.Length == 0) continue;
            // an area that fits in this scope's frames (as they are turned against it) is best served by centred shots, not raster passes
            bool fits = mine.Any(f => f.OffsetEast == 0 && f.OffsetNorth == 0 && Covers(f, f.Rotation - r.PositionAngleDegrees.Value, plan.Width, plan.Height));
            double target; lock (_gate) target = TargetOf(l, depth ?? _target);
            var p = new CoveragePlanner(plan.Map, mine, [0.0], r.PositionAngleDegrees.Value, l.Exposure, target, plan.Stepover, l.Index)
            { TopUpOnly = fits, PaintFrames = masked.Where(f => f.Mask != 0).ToArray(), MinInside = Math.Clamp(r.MinInsideShare.Value, 0, 0.9), Hilbert = string.Equals(r.Path.Text, "Hilbert", StringComparison.OrdinalIgnoreCase) };
            // until stopped: deeper a step (about an hour of shots) at a time, in even passes over all of it
            if (target <= 0) { p.KeepGoing = true; p.TargetSeconds = p.HourOfDepth(); p.Plan(); }
            else if (fits) p.Plan();
            list.Add((l, p));
        }
        return list;
    }

    /// <summary>Whether a frame turned <paramref name="thetaDegrees"/> against the area's axes covers a centred area of that size whole.</summary>
    public static bool Covers(FrameSpec f, double thetaDegrees, double width, double height)
    {
        double t = thetaDegrees * Math.PI / 180, c = Math.Abs(Math.Cos(t)), s = Math.Abs(Math.Sin(t));
        return width / 2 * c + height / 2 * s <= f.HalfWidth * (1 + 1e-9) && width / 2 * s + height / 2 * c <= f.HalfHeight * (1 + 1e-9);
    }

    /// <summary>The largest area upright on the area's axes, of the frame's proportions (turned a quarter when the frame lies more
    /// across than along), that a frame turned <paramref name="thetaDegrees"/> against those axes covers whole.</summary>
    public static (double Width, double Height) InscribedUpright(FrameSpec f, double thetaDegrees)
    {
        double t = thetaDegrees * Math.PI / 180, c = Math.Abs(Math.Cos(t)), s = Math.Abs(Math.Sin(t));
        double w0 = c >= s ? f.HalfWidth : f.HalfHeight, h0 = c >= s ? f.HalfHeight : f.HalfWidth;
        double k = Math.Min(f.HalfWidth / (w0 * c + h0 * s), f.HalfHeight / (w0 * s + h0 * c));
        return (2 * k * w0, 2 * k * h0);
    }

    private ShooterExposure ExposureOf(ImagingRequest r, LayerPlan l)
    {
        Span<byte> bytes = r.Exposure.ToBytes();
        var e = new ShooterExposure(); e.FromBytes(ref bytes);
        e.Seconds = l.Exposure;
        if (l.Filter != "") e.Filter = l.Filter;
        return e;
    }

    /// <summary>One scope's worker: next spot for its frames, a dithered shot there, painted when it is done.</summary>
    private async Task WorkAsync(ImagingRequest r, Plan plan, ScopeFrames scope, List<(LayerPlan Layer, CoveragePlanner Planner)> planners, RemoteState<WeatherState>? weather, CancellationToken ct)
    {
        string id = scope.ScopeId;
        using var state = new RemoteState<ScopeState>(_node, ScopeIds.State(id), ScopeIds.GetState(id));
        await state.StartAsync();
        double dither = r.DitherArcsec.Value / 3600;
        int rejectedInARow = 0, failedInARow = 0;
        using var pointer = new RemoteState<PointerState>(_node, PointerIds.State(id), PointerIds.GetState(id));
        await pointer.StartAsync();
        // where the scope really lands is learned from its frames (solved while the next one exposes): a steady pointing error is
        // aimed off, and the map is painted where the frames are, so what a shot missed is filled in later. No re-centring.
        var landing = new Landing(this, r, plan, id, scope);
        (double X, double Y)? lastSent = null;
        var exhausted = new HashSet<CoveragePlanner>();      // nothing left for a shot of this layer to improve (until its depth changes or a shot is rejected)
        (LayerPlan Layer, CoveragePlanner Planner)? current = null;
        if (planners.Count == 0) { await SetWorker(id, w => { w.Phase = "Done"; w.Message = "none of its cameras feeds a layer"; }); return; }
        while (!ct.IsCancellationRequested)
        {
            await WaitUntilAllowedAsync(weather, ct);
            Pose? pose = null;
            (LayerPlan Layer, CoveragePlanner Planner) chosen = planners[0];
            lock (_gate)
            {
                if (r.MaxVisits.Value > 0 && _visitsStarted >= r.MaxVisits.Value) pose = null;
                else
                {
                    // a new depth while running: this scope's plan is redone from what is still missing
                    foreach (var (l, p) in planners)
                        // a new depth asked for while running (a planner that keeps going only follows it upwards)
                        if (TargetOf(l) <= 0 && !p.KeepGoing)
                        {
                            // switched to "until stopped": keep going a step at a time from here
                            p.KeepGoing = true; p.TargetSeconds = Math.Max(p.Map.Min(p.Layer), 0) + p.HourOfDepth(); p.Plan(); exhausted.Remove(p);
                        }
                        else if (p.KeepGoing ? TargetOf(l) > p.TargetSeconds : p.TargetSeconds != TargetOf(l)) { p.KeepGoing = false; p.TargetSeconds = TargetOf(l); p.Plan(); exhausted.Remove(p); }
                    foreach (var candidate in Candidates(planners, exhausted, current))
                    {
                        pose = candidate.Planner.Next();
                        if (pose is not null) { chosen = candidate; current = candidate; _visitsStarted++; break; }
                        exhausted.Add(candidate.Planner);
                    }
                }
            }
            if (pose is not { } planned) break;
            var planner = chosen.Planner;
            var exposure = ExposureOf(r, chosen.Layer);
            // unguided dithering: land anywhere within the dither radius of the planned spot
            double a = _random.NextDouble() * 2 * Math.PI, rr = dither * Math.Sqrt(_random.NextDouble());
            var at = planned with { X = planned.X + rr * Math.Cos(a), Y = planned.Y + rr * Math.Sin(a) };
            await SetWorker(id, w => { w.Phase = "Shooting"; w.PoseX = at.X; w.PoseY = at.Y; w.Message = ""; });
            var (aimX, aimY) = landing.AimOff;
            var sent = at with { X = at.X - aimX, Y = at.Y - aimY };
            // backlash: short hops along a row arrive from the row's direction; anything else (a new row, a jump) first goes to a
            // point a little before the target on that side, so this shot also arrives the same way
            if (lastSent is { } ls && Approach(r, id, ls, sent) is { } lead)
                await LeadInAsync(r, id, pointer, lead, ct);
            lastSent = (sent.X, sent.Y);
            var (error, frames) = await ShootAsync(r, id, state, sent, exposure, ct);
            double exp = planner.ExposureSeconds;
            if (error.StartsWith(RejectedMark))
            {
                // the scope judged the frame bad: it does not count; the spot goes back to the plan
                lock (_gate) { plan.Map.Paint(planner.PaintFootprints(planned), -exp); _visitsStarted--; exhausted.Clear(); }
                string why = error[RejectedMark.Length..];
                rejectedInARow++;
                await SetWorker(id, w => { w.Rejected = w.Rejected.Value + 1; w.Message = "rejected: " + why; });
                if (rejectedInARow >= 3)
                {
                    await SetWorker(id, w => { w.Phase = "WaitingForSky"; w.Message = $"{rejectedInARow} bad frames in a row ({why}): waiting {r.SkyWaitSeconds.Value:0} s"; });
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, r.SkyWaitSeconds.Value)), ct);
                }
                continue;
            }
            rejectedInARow = 0;
            if (error != "")
            {
                // give the spot back to the plan: another scope may take it
                lock (_gate) { plan.Map.Paint(planner.PaintFootprints(planned), -exp); _visitsStarted--; exhausted.Clear(); }
                failedInARow++;
                if (failedInARow >= MaxFailuresInARow || ct.IsCancellationRequested)
                {
                    SessionLog.Error(_node, id, $"gave up on {r.Label.Text} after {failedInARow} failed shots in a row: {error}");
                    await SetWorker(id, w => { w.Phase = "Failed"; w.Message = error; });
                    return;
                }
                // a missed frame, a mount that did not answer, a camera that hiccupped: try again after a pause, the night is not over
                SessionLog.Warn(_node, id, $"shot failed ({error}); trying again in {RetryPause.TotalSeconds:0} s ({failedInARow} of {MaxFailuresInARow})");
                await SetWorker(id, w => { w.Phase = "Waiting"; w.Message = $"retrying after: {error}"; });
                try { await Task.Delay(RetryPause, ct); } catch (OperationCanceledException) { break; }
                lastSent = null;
                continue;
            }
            failedInARow = 0;
            lock (_gate)
            {
                // the shot landed at the dithered spot: the plan learns where it really went, the real map gets it
                var real = planner.PaintFootprints(at);
                plan.Map.Paint(planner.PaintFootprints(planned), -exp);
                plan.Map.Paint(real, exp);
                plan.Actual.Paint(real, exp);
                plan.Gathered?.Paint(real, exp);
            }
            landing.Solve(frames, at, sent, planner, exp);
            await SetWorker(id, w => w.Visits = w.Visits.Value + 1);
            await Set(s => s.Visits = s.Visits.Value + 1);
            Keep(r, plan);
        }
        await landing.DrainAsync();
        if (!ct.IsCancellationRequested) await SetWorker(id, w => { w.Phase = "Done"; w.Message = ""; });
    }

    /// <summary>The layers a scope could shoot for, best first: the least advanced (by its depth), but staying on the filter in hand
    /// (changing filters costs time) unless another is clearly behind (by a third of its depth).</summary>
    private static List<(LayerPlan Layer, CoveragePlanner Planner)> Candidates(List<(LayerPlan Layer, CoveragePlanner Planner)> planners,
        HashSet<CoveragePlanner> exhausted, (LayerPlan Layer, CoveragePlanner Planner)? current)
    {
        double Progress((LayerPlan, CoveragePlanner Planner) x) => x.Planner.TargetSeconds > 0 ? x.Planner.Map.Mean(x.Planner.Layer) / x.Planner.TargetSeconds : x.Planner.Map.Mean(x.Planner.Layer);
        var open = planners.Where(x => !exhausted.Contains(x.Planner)).OrderBy(Progress).ToList();
        if (open.Count > 1 && current is { } c && open.Any(x => x.Planner == c.Planner))
        {
            var best = open[0];
            bool sameFilter = string.Equals(best.Layer.Filter, c.Layer.Filter, StringComparison.OrdinalIgnoreCase);
            if (!sameFilter && Progress(c) <= Progress(best) + 0.3) { open.RemoveAll(x => x.Planner == c.Planner); open.Insert(0, c); }
        }
        return open;
    }

    private const string RejectedMark = "!rejected:";

    /// <summary>One shot through the scope. "" when done and good; RejectedMark + why when every frame of it was rejected by
    /// the scope's grading; anything else is an error.</summary>
    private async Task<(string Error, List<ShotEvent> Frames)> ShootAsync(ImagingRequest r, string scope, RemoteState<ScopeState> state, Pose at, ShooterExposure exposure, CancellationToken ct)
    {
        var frames = new List<ShotEvent>();
        Action<ShotEvent> onShot = s => { lock (frames) frames.Add(s); };
        await _node.HookEventAsync(ShooterIds.Shot(scope), onShot, "image request: frame grades");
        try { return (await ShootOnceAsync(r, scope, state, at, exposure, frames, ct), frames); }
        finally { try { _node.UnhookEvent(ShooterIds.Shot(scope), onShot); } catch (ObjectDisposedException) { } }
    }

    /// <summary>Failed shots in a row before a scope gives up on the image (each is retried after <see cref="RetryPause"/>).</summary>
    public int MaxFailuresInARow { get; set; } = 5;
    public TimeSpan RetryPause { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How far before the target a lead-in move stops (degrees; 0 = no lead-in).</summary>
    public double ApproachDegrees { get; set; } = 0.2;

    /// <summary>The point on the sky to go to first, when a move would not arrive the way a row's hops do (+x along the image);
    /// null when it does. With the mount's backlash measured, this is decided axis by axis: an axis with slack that would turn
    /// round by more than a little gets a lead-in from a few times its slack before the target, and one without slack is not
    /// bothered (dither hops back and forth cost nothing there). Unmeasured, it is decided along the image's rows.</summary>
    private (double RaHours, double DecDegrees)? Approach(ImagingRequest r, string scope, (double X, double Y) from, Pose to)
    {
        if (!(ApproachDegrees > 0)) return null;
        double ra0 = r.Center.RaHours.Value, dec0 = r.Center.DecDegrees.Value, pa = r.PositionAngleDegrees.Value;
        BacklashMeasurement? b; lock (_gate) _backlash.TryGetValue(scope, out b);
        if (b is null)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6 || dx >= 0.8 * len) return null;
            return PlanProjection.ToSky(ra0, dec0, pa, to.X - ApproachDegrees, to.Y);
        }
        var (raF, decF) = PlanProjection.ToSky(ra0, dec0, pa, from.X, from.Y);
        var (raT, decT) = PlanProjection.ToSky(ra0, dec0, pa, to.X, to.Y);
        var (sRa, sDec) = RowSigns(r);
        double dRa = (((raT - raF + 12) % 24 + 24) % 24 - 12) * 15, dDec = decT - decF;      // degrees of each axis
        double slackRa = Math.Abs(b.RaArcsec.Value), slackDec = Math.Abs(b.DecArcsec.Value);
        bool ra = double.IsFinite(slackRa) && slackRa >= MinSlackArcsec, dec = double.IsFinite(slackDec) && slackDec >= MinSlackArcsec;
        bool turns = ra && dRa * sRa < -Math.Max(10, slackRa / 4) / 3600 || dec && dDec * sDec < -Math.Max(10, slackDec / 4) / 3600;
        if (!turns) return null;
        double aRa = ra ? Math.Max(3 * slackRa / 3600, 0.02) : 0, aDec = dec ? Math.Max(3 * slackDec / 3600, 0.02) : 0;
        return (((raT - sRa * aRa / 15) % 24 + 24) % 24, decT - sDec * aDec);
    }

    /// <summary>Slack below this (arcseconds) is not worth a lead-in: it is about what the solves measure it to.</summary>
    public double MinSlackArcsec { get; set; } = 8;

    /// <summary>Which way a row's hops (+x) move each mount axis (+1 or -1; +1 when they hardly move it).</summary>
    private static (int Ra, int Dec) RowSigns(ImagingRequest r)
    {
        double ra0 = r.Center.RaHours.Value, dec0 = r.Center.DecDegrees.Value, pa = r.PositionAngleDegrees.Value;
        var a = PlanProjection.ToSky(ra0, dec0, pa, 0, 0);
        var c = PlanProjection.ToSky(ra0, dec0, pa, 0.1, 0);
        double dRa = (((c.RaHours - a.RaHours + 12) % 24 + 24) % 24 - 12) * 15 * Math.Cos(dec0 * Math.PI / 180), dDec = c.DecDegrees - a.DecDegrees;
        return (dRa < -0.005 ? -1 : 1, dDec < -0.005 ? -1 : 1);
    }

    private async Task LeadInAsync(ImagingRequest r, string scope, RemoteState<PointerState> pointer, (double RaHours, double DecDegrees) lead, CancellationToken ct)
    {
        try
        {
            var go = await Commands.CallAsync(_node, PointerIds.Goto(scope), new SkyTarget { RaHours = lead.RaHours, DecDegrees = lead.DecDegrees, Epoch = "J2000" });
            if (!go.Ok.Value) return;                     // only a refinement: the shot goes ahead anyway
            await Task.Delay(500, ct);
            await pointer.WaitAsync(p => p.OnTarget.Value || p.Phase.Text is "Error" or "Parked" or "Disconnected", TimeSpan.FromSeconds(Math.Max(10, r.SlewTimeoutSeconds.Value)), ct);
        }
        catch (TimeoutException) { }
    }

    // ---- backlash -------------------------------------------------------------------------------------------------

    /// <summary>Measure each mount's slack at the first image of the station's run (a few plate-solved moves each way, a few
    /// minutes); later images, and any mount that cannot be measured now, use the last measurement kept.</summary>
    public bool TrainBacklash { get; set; } = true;
    /// <summary>A measurement younger than this is used as it is.</summary>
    public TimeSpan BacklashMaxAge { get; set; } = TimeSpan.FromHours(12);
    private readonly Dictionary<string, BacklashMeasurement> _backlash = new();     // by scope
    private readonly HashSet<string> _trained = new();                               // mounts measured since the station started

    private async Task KeepFramesAsync(Plan plan)
    {
        foreach (var sc in plan.Scopes)
        {
            try
            {
                var answers = await _node.CallFunctionAsync<StorageWatch, CommandResult>(StorageIds.Watch, new StorageWatch { ShooterId = sc.ScopeId, Enabled = true }, TimeSpan.FromSeconds(10));
                if (answers is null || answers.Count == 0) SessionLog.Warn(_node, sc.ScopeId, "no storage on the mesh: this image's frames are not saved to disk");
                else if (answers.FirstOrDefault(a => !a.Ok.Value) is { } bad) SessionLog.Warn(_node, sc.ScopeId, "frames are not saved: " + bad.Error.Text);
            }
            catch (Exception ex) { SessionLog.Warn(_node, sc.ScopeId, "frames may not be saved: " + ex.Message); }
        }
    }

    private async Task TrainBacklashAsync(Plan plan, CancellationToken ct)
    {
        BacklashState? known;
        try { known = (await _node.CallFunctionAsync<NOTESVoid, BacklashState>(BacklashIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(5), ct))?.FirstOrDefault(); }
        catch (Exception) when (!ct.IsCancellationRequested) { known = null; }
        if (known is null) return;                       // no backlash service on the mesh
        CompositionSnapshot snap;
        try { snap = await SnapshotAsync(); } catch (Exception) { return; }
        var byMount = plan.Scopes.GroupBy(s => snap.Scopes.FirstOrDefault(d => d.Id.Text == s.ScopeId) is { Pointers.Count: > 0 } d ? d.Pointers[0].Text : s.ScopeId);
        await Task.WhenAll(byMount.Select(async g =>
        {
            string mount = g.Key, scope = g.First().ScopeId;
            var m = known.Mounts.FirstOrDefault(x => x.MountId.Text == mount);
            // once a night: a measurement of the last hours (a restart in the night) is still good
            bool fresh = m is { Ok.Value: true } && DateTime.TryParse(m.MeasuredUtc.Text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && DateTime.UtcNow - at.ToUniversalTime() < BacklashMaxAge;
            bool measure; lock (_gate) measure = TrainBacklash && !fresh && _trained.Add(mount);
            if (measure)
            {
                foreach (var s in g) await SetWorker(s.ScopeId, w => { w.Phase = "Waiting"; w.Message = "measuring the mount's backlash (a few moves each way)"; });
                var got = (await _node.CallFunctionAsync<BinaryConvertibleString, BacklashMeasurement>(BacklashIds.Measure, scope, TimeSpan.FromMinutes(20), ct))?.FirstOrDefault();
                if (got is { Ok.Value: true }) m = got;
                else if (m is not null) SessionLog.Info(_node, scope, $"using the backlash measured {m.MeasuredUtc.Text}: {m.Message.Text}");
                foreach (var s in g) await SetWorker(s.ScopeId, w => w.Message = "");
            }
            if (m is { Ok.Value: true }) lock (_gate) foreach (var s in g) _backlash[s.ScopeId] = m;
        }));
    }

    private async Task<string> ShootOnceAsync(ImagingRequest r, string scope, RemoteState<ScopeState> state, Pose at, ShooterExposure exposure, List<ShotEvent> frames, CancellationToken ct)
    {
        var (ra, dec) = PlanProjection.ToSky(r.Center.RaHours.Value, r.Center.DecDegrees.Value, r.PositionAngleDegrees.Value, at.X, at.Y);
        var start = await Commands.CallAsync(_node, ScopeIds.Command(scope, "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" }, Exposure = exposure, Count = 1,
            SlewTimeoutSeconds = r.SlewTimeoutSeconds.Value, ObjectName = r.Label.Text, PlanId = r.Label.Text,
        });
        if (!start.Ok.Value) return start.Error.Text;
        var end = await state.WaitAsync(s => !s.Observing.Value, TimeSpan.FromHours(6), ct);
        if (end.Phase.Text == "Error") return end.Message.Text != "" ? end.Message.Text : "the scope reported an error";
        if (end.ShotsDone.Value < 1) return "the shot was not taken";
        List<ShotEvent> got; lock (frames) got = frames.ToList();
        if (got.Count > 0 && got.All(f => f.Quality.Text == "Rejected")) return RejectedMark + got[0].QualityNote.Text;
        return "";
    }

    /// <summary>Cameras whose angle on the sky is not known yet (no solve since they were set up): one plate solve each,
    /// if a solver is on the mesh, so the plan lays their frames out as they really are. The solve is of where the scope points
    /// now (it need not be at the target: only the camera's angle is wanted), with the camera's scale as a hint, so it is quick.
    /// Failures only cost accuracy: the plan then assumes no turn, and says so.</summary>
    private async Task LearnAnglesAsync(ImagingRequest r)
    {
        try
        {
            var snap = await SnapshotAsync();
            var unknown = new List<(string Shooter, string Scope)>();
            var scaleOf = new Dictionary<string, double>();
            foreach (var id in r.ScopeIds.Select(s => s.Text).Distinct())
                if (await FramesOfAsync(id, snap) is { Error: null } f)
                {
                    unknown.AddRange(f.UnknownAngles.Select(u => (u, id)));
                    foreach (var (k, v) in f.Scales) scaleOf[k] = v;
                }
            if (unknown.Count == 0) return;
            await Set(s => s.Message = $"learning the camera angle of {string.Join(", ", unknown.Select(u => u.Shooter).Distinct())}");
            var solves = unknown.GroupBy(u => u.Shooter).Select(async g =>
            {
                double ra = double.NaN, dec = double.NaN, radius = 10;
                var pointer = (await _node.CallFunctionAsync<NOTESVoid, PointerState>(PointerIds.GetState(g.First().Scope), NOTESVoid.Void, TimeSpan.FromSeconds(5)))?.FirstOrDefault();
                if (pointer is { Phase.Text: not ("Disconnected" or "Error") }) { ra = pointer.RaHours.Value; dec = pointer.DecDegrees.Value; }
                var answers = await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
                {
                    ShooterId = g.Key, ExposureSeconds = Math.Clamp(r.Exposure.Seconds.Value, 1, 10), TimeoutSeconds = 45,
                    HintRaHours = ra, HintDecDegrees = dec, HintRadiusDegrees = radius,
                    // the camera's scale is known from its pixel size and the telescope: a narrow range makes the solve fast
                    ScaleLowArcsecPerPixel = scaleOf.TryGetValue(g.Key, out var sc) && sc > 0 ? sc * 0.8 : 0, ScaleHighArcsecPerPixel = scaleOf.TryGetValue(g.Key, out var sc2) && sc2 > 0 ? sc2 * 1.25 : 0,
                }, TimeSpan.FromSeconds(120));
                return (g.Key, Solved: answers?.FirstOrDefault()?.Solved.Value == true);
            }).ToList();
            var results = await Task.WhenAll(solves);
            await Task.Delay(300);   // the trains publish what they learned
            var failed = results.Where(x => !x.Solved).Select(x => x.Key).ToList();
            if (failed.Count > 0) await Set(s => s.Message = $"could not learn the camera angle of {string.Join(", ", failed)}: planning as if it were not turned");
        }
        catch (Exception) { }
    }

    // ---- keeping images for another night ------------------------------------------------------------------------

    private sealed record Kept(ImagingRequest Request, double Width, double Height, int Cols, int Rows, float[] Seconds, int Visits, string UpdatedUtc, int Layers, double OverX = 0, double OverY = 0, int GatheredCols = 0, int GatheredRows = 0, float[]? Gathered = null);
    // OverX/OverY: how far the live stack reaches beyond the area (0 in images kept before the stack held the overhang)
    private sealed record KeptMeta(double Width, double Height, int Cols, int Rows, int Visits, string UpdatedUtc, int Layers = 1, double OverX = 0, double OverY = 0, int GatheredCols = 0, int GatheredRows = 0);

    private static string Safe(string label) => new(label.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>After every good shot: the request, the real coverage and the count, written aside and moved in place.</summary>
    private void Keep(ImagingRequest r, Plan plan)
    {
        string? dir; int visits; float[] seconds; float[]? gathered;
        lock (_gate) { dir = _runDir; visits = _state.Visits.Value; seconds = plan.Actual.Flatten(); gathered = plan.Gathered?.Flatten(); }
        if (dir is null) return;
        try
        {
            Directory.CreateDirectory(dir);
            void Write(string name, byte[] bytes) { string p = Path.Combine(dir, name); File.WriteAllBytes(p + ".part", bytes); File.Move(p + ".part", p, true); }
            Write("request.bin", r.ToBytes());
            Write("coverage.bin", System.Runtime.InteropServices.MemoryMarshal.AsBytes(seconds.AsSpan()).ToArray());
            if (gathered is not null) Write("gathered.bin", System.Runtime.InteropServices.MemoryMarshal.AsBytes(gathered.AsSpan()).ToArray());
            Write("image.json", System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
                new KeptMeta(plan.Width, plan.Height, plan.Actual.Cols, plan.Actual.Rows, visits, DateTime.UtcNow.ToString("o"), plan.Actual.Layers, plan.OverX, plan.OverY, plan.Gathered?.Cols ?? 0, plan.Gathered?.Rows ?? 0))));
        }
        catch (Exception ex) { lock (_gate) _state.Message = "could not keep the image: " + ex.Message; }
    }

    private static Kept? LoadKept(string dir)
    {
        try
        {
            var meta = System.Text.Json.JsonSerializer.Deserialize<KeptMeta>(File.ReadAllText(Path.Combine(dir, "image.json")))!;
            Span<byte> req = File.ReadAllBytes(Path.Combine(dir, "request.bin"));
            var r = new ImagingRequest(); r.FromBytes(ref req);
            var bytes = File.ReadAllBytes(Path.Combine(dir, "coverage.bin"));
            var seconds = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            if (seconds.Length != meta.Cols * meta.Rows * Math.Max(1, meta.Layers)) return null;
            float[]? gathered = null;
            string g = Path.Combine(dir, "gathered.bin");
            if (meta.GatheredCols > 0 && File.Exists(g))
            {
                gathered = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(g)).ToArray();
                if (gathered.Length != meta.GatheredCols * meta.GatheredRows * Math.Max(1, meta.Layers)) gathered = null;
            }
            return new Kept(r, meta.Width, meta.Height, meta.Cols, meta.Rows, seconds, meta.Visits, meta.UpdatedUtc, Math.Max(1, meta.Layers), meta.OverX, meta.OverY, meta.GatheredCols, meta.GatheredRows, gathered);
        }
        catch (Exception) { return null; }
    }

    private SavedImages ListSaved()
    {
        var list = new SavedImages();
        if (_dataDir is null || !Directory.Exists(Path.Combine(_dataDir, "images"))) return list;
        foreach (var dir in Directory.GetDirectories(Path.Combine(_dataDir, "images")).Order())
            if (LoadKept(dir) is { } k)
                list.Images.Add(new SavedImage
                {
                    Label = k.Request.Label.Text, Request = k.Request, Visits = k.Visits, UpdatedUtc = k.UpdatedUtc,
                    MinSeconds = k.Seconds.Length > 0 ? k.Seconds.Min() : 0, MeanSeconds = k.Seconds.Length > 0 ? k.Seconds.Average() : 0,
                });
        return list;
    }

    private CommandResult DeleteSaved(string label)
    {
        if (_dataDir is null) return CommandResult.Fail("images are not kept here");
        string dir = Path.Combine(_dataDir, "images", Safe(label));
        lock (_gate) if (_runDir == dir && _cts is not null) return CommandResult.Fail("that image is being taken now");
        if (!Directory.Exists(dir)) return CommandResult.Fail($"no kept image '{label}'");
        Directory.Delete(dir, true);
        return CommandResult.Success();
    }

    private async Task WaitUntilAllowedAsync(RemoteState<WeatherState>? weather, CancellationToken ct)
    {
        while (true)
        {
            bool paused; lock (_gate) paused = _paused;
            bool unsafeSky = weather?.Latest is { Safety.Text: "Unsafe" };
            if (!paused && !unsafeSky) { await Set(s => { if (s.Phase.Text is "Paused" or "WaitingForWeather") { s.Phase = "Running"; s.Message = ""; } }); return; }
            await Set(s => { s.Phase = paused ? "Paused" : "WaitingForWeather"; s.Message = paused ? "paused" : "weather is unsafe"; });
            await Task.Delay(500, ct);
        }
    }

    // ---- commands -------------------------------------------------------------------------------------------------

    private async Task<CommandResult> SetPaused(bool paused)
    {
        lock (_gate) { if (_cts is null) return CommandResult.Fail("nothing is being imaged"); _paused = paused; }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private Task<CommandResult> AbortAsync()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                // stopping an image that waits to carry on after a restart: it does not
                if (_resume is { IsCompleted: false }) { _life.Cancel(); ClearRunning(); _ = Set(s => { s.Phase = "Idle"; s.Message = "it will not carry on by itself; it is kept"; }); return Task.FromResult(CommandResult.Success()); }
                return Task.FromResult(CommandResult.Fail("nothing is being imaged"));
            }
            _stoppedByPerson = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        return Task.FromResult(CommandResult.Success());
    }

    private async Task<CommandResult> SetTargetAsync(BinaryConvertibleDouble seconds)
    {
        if (seconds.Value < 0 || double.IsNaN(seconds.Value)) return CommandResult.Fail("the target cannot be negative");
        lock (_gate) { if (_cts is null) return CommandResult.Fail("nothing is being imaged"); _target = seconds.Value; }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    // ---- state ----------------------------------------------------------------------------------------------------

    private async Task Set(Action<ImagingState> change)
    {
        lock (_gate) change(_state);
        try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { }
    }

    private async Task SetWorker(string id, Action<ImagingWorker> change)
    {
        lock (_gate) if (_workers.TryGetValue(id, out var w)) change(w);
        try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { }
    }

    private ImagingState BuildState()
    {
        lock (_gate)
        {
            var s = new ImagingState
            {
                Phase = _state.Phase.Text, Label = _state.Label.Text, Message = _state.Message.Text, Visits = _state.Visits.Value, TargetSeconds = _target,
                WidthDegrees = _state.WidthDegrees.Value, HeightDegrees = _state.HeightDegrees.Value,
            };
            foreach (var w in _workers.Values)
            {
                var c = new ImagingWorker { ScopeId = w.ScopeId.Text, Phase = w.Phase.Text, Message = w.Message.Text, Visits = w.Visits.Value, Rejected = w.Rejected.Value, PoseX = w.PoseX.Value, PoseY = w.PoseY.Value };
                foreach (var f in w.Frames) c.Frames.Add(f);
                s.Workers.Add(c);
            }
            if (_plan?.Actual is { } map)
            {
                var layers = _plan.Layers;
                s.MinSeconds = Enumerable.Range(0, map.Layers).Min(l => map.Min(l)); s.MeanSeconds = Enumerable.Range(0, map.Layers).Average(l => map.Mean(l)); s.MaxSeconds = Enumerable.Range(0, map.Layers).Max(l => map.Max(l));
                byte[] bytes; int cols, rows;
                if (map.Layers == 1) bytes = map.Render(Math.Max(TargetOf(layers[0]), map.Max()), 96, 64, out cols, out rows);
                else bytes = map.RenderProgress(layers.Select(l => TargetOf(l)).ToList(), 96, 64, out cols, out rows);
                s.MapCols = cols; s.MapRows = rows; s.Map = new RawBytes(bytes);
                if (_plan.Gathered is { } all)
                {
                    // all the sky the frames reached, on the same scale of depth as the area's map
                    double top = map.Layers == 1 ? Math.Max(TargetOf(layers[0]), map.Max()) : Math.Max(layers.Max(TargetOf), map.Max());
                    var gb = all.Render(top, 128, 96, out int gc, out int gr);
                    s.GatheredWidthDegrees = all.FovWidth; s.GatheredHeightDegrees = all.FovHeight; s.GatheredCols = gc; s.GatheredRows = gr; s.Gathered = new RawBytes(gb);
                }
                if (layers.Count > 1 || layers[0].Label != "")
                    foreach (var l in layers)
                        s.Layers.Add(new ImagingLayerState
                        {
                            Label = l.Label, Filter = l.Filter, TargetSeconds = TargetOf(l), MinSeconds = map.Min(l.Index), MeanSeconds = map.Mean(l.Index),
                            Scopes = _plan.Scopes.Count(sc => sc.Frames.Any(l.Fits)),
                        });
            }
            return s;
        }
    }

    private static ImagingRequest Copy(ImagingRequest r)
    {
        Span<byte> bytes = r.ToBytes();
        var c = new ImagingRequest();
        c.FromBytes(ref bytes);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();       // first: the image ends because the station stops, so it carries on when it starts again
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        if (_resume is not null) await Task.WhenAny(_resume, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
