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
public sealed class ImagingService : IAsyncDisposable
{
    private sealed record ScopeFrames(string ScopeId, FrameSpec[] Frames, double FinestScale, string? Error, List<string> UnknownAngles, Dictionary<string, double> Scales);
    private sealed record Plan(CoverageMap Map, CoverageMap Actual, List<ScopeFrames> Scopes, double Width, double Height, double Stepover, double Scale);

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
                        frames.Add(new FrameSpec(c.FieldWidthDegrees.Value / 2, c.FieldHeightDegrees.Value / 2, angle, oe, on));
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
        var all = scopes.SelectMany(s => s.Frames).ToList();
        double minFrame = all.Min(f => 2 * Math.Min(f.HalfWidth, f.HalfHeight));
        var smallest = all.OrderBy(f => f.HalfWidth * f.HalfHeight).First();
        // a target with no size is one frame of the smallest train
        // (a little smaller than the frame, so dithered shots still cover all of it)
        double w = r.WidthDegrees.Value > 0 ? r.WidthDegrees.Value : 2 * smallest.HalfWidth * 0.9;
        double h = r.HeightDegrees.Value > 0 ? r.HeightDegrees.Value : 2 * smallest.HalfHeight * 0.9;
        if (Math.Abs(r.Center.DecDegrees.Value) + Math.Max(w, h) / 2 + 2 * all.Max(f => Math.Max(f.HalfWidth, f.HalfHeight)) > 89.5)
            return (null, "the area reaches a celestial pole, where the tiling is undefined");
        double stepover = r.StepoverDegrees.Value > 0 ? r.StepoverDegrees.Value : 0.1 * minFrame;
        double cell = Math.Max(minFrame / 12, Math.Sqrt(w * h / 40000.0));
        var map = new CoverageMap(w, h, cell);
        if (kept is not null)
        {
            // carrying on: the same area, and the coverage it already has, on the grid it was kept on
            if (Math.Abs(kept.Width - w) > 1e-9 || Math.Abs(kept.Height - h) > 1e-9) return (null, $"the kept image '{r.Label.Text}' covers another area ({kept.Width:0.###}° x {kept.Height:0.###}°): start it afresh or use another name");
            map = CoverageMap.Restore(w, h, kept.Cols, kept.Rows, kept.Seconds);
        }
        double scale = r.OutputPixelScaleArcsec.Value > 0 ? r.OutputPixelScaleArcsec.Value : scopes.Where(s => !double.IsNaN(s.FinestScale)).Select(s => s.FinestScale).DefaultIfEmpty(0).Min();
        return (new Plan(map, map.Clone(), scopes, w, h, stepover, scale), null);
    }

    private async Task<ImagingState> PreviewAsync(ImagingRequest r)
    {
        var (plan, error) = await ResolveAsync(r);
        var s = new ImagingState { Phase = error is null ? "Idle" : "Error", Message = error ?? "", Label = r.Label.Text, TargetSeconds = r.TargetSeconds.Value };
        if (plan is null) return s;
        s.WidthDegrees = plan.Width; s.HeightDegrees = plan.Height;
        foreach (var sc in plan.Scopes) s.Workers.Add(Worker(sc));
        s.Message = FormattableString.Invariant($"{plan.Width:0.###}° x {plan.Height:0.###}°, stepover {plan.Stepover:0.####}°") + (plan.Scale > 0 ? FormattableString.Invariant($", image at {plan.Scale:0.##}\"/px") : "");
        return s;
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
            _plan = plan; _target = r.TargetSeconds.Value; _paused = false; _visitsStarted = 0;
            _workers.Clear();
            foreach (var sc in plan.Scopes) _workers[sc.ScopeId] = Worker(sc);
            _state = new ImagingState { Phase = "Running", Label = r.Label.Text, WidthDegrees = plan.Width, HeightDegrees = plan.Height, Visits = kept?.Visits ?? 0 };
            if (kept is not null) _state.Message = $"carrying on: {kept.Visits} shots kept from before";
            _runDir = runDir;
        }
        if (r.LiveStack.Value)
        {
            var stack = new LiveStackRequest
            {
                Label = r.Label.Text, Center = new SkyTarget { RaHours = r.Center.RaHours.Value, DecDegrees = r.Center.DecDegrees.Value, Epoch = "J2000" },
                // the stack is a tangent grid: it must be as wide as the area's edges are on the tangent plane (see PlanProjection)
                FovWidthDegrees = PlanProjection.TangentSpan(plan.Width), FovHeightDegrees = PlanProjection.TangentSpan(plan.Height), PositionAngleDegrees = r.PositionAngleDegrees.Value, PixelScaleArcsec = plan.Scale,
                // with one kind of frame, its scale lets frames be placed by their pointing when they cannot be solved
                FramePixelScaleArcsec = plan.Scopes.Select(s => s.FinestScale).Distinct().Count() == 1 && plan.Scopes[0].FinestScale > 0 ? plan.Scopes[0].FinestScale : 0,
                SessionKey = runDir is null ? "" : r.Label.Text, Resume = r.Resume.Value,
            };
            foreach (var sc in plan.Scopes) stack.ShooterIds.Add(sc.ScopeId);
            var started = await Commands.CallAsync(_node, LiveStackIds.Start, stack);
            if (!started.Ok.Value) await Set(s => s.Message = "no live stack: " + started.Error.Text);
        }
        await _publisher.PublishAsync();
        var copy = Copy(r);
        Keep(copy, plan);
        _run = Task.Run(() => RunAsync(copy, plan, cts));
        return CommandResult.Success();
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
            // one planner per scope (its own frames), all painting the same map: a spot one scope covered is skipped by the others
            var planners = plan.Scopes.ToDictionary(s => s.ScopeId, s =>
            {
                // an area that fits in this scope's frames is best served by centred shots, not raster passes
                bool fits = s.Frames.Any(f => f.OffsetEast == 0 && f.OffsetNorth == 0 && plan.Width <= 2 * f.HalfWidth && plan.Height <= 2 * f.HalfHeight);
                var p = new CoveragePlanner(plan.Map, s.Frames, [0.0], r.PositionAngleDegrees.Value, r.Exposure.Seconds.Value, r.TargetSeconds.Value, plan.Stepover) { TopUpOnly = fits };
                if (fits) p.Plan();
                return p;
            });
            await Task.WhenAll(plan.Scopes.Select(s => Task.Run(() => WorkAsync(r, plan, s, planners[s.ScopeId], weather, ct))));
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var ws = _workers.Values.ToList();
                if (ws.All(w => w.Phase.Text == "Failed")) { endPhase = "Error"; endMessage = string.Join("; ", ws.Select(w => $"{w.ScopeId.Text}: {w.Message.Text}")); }
                else if (ws.Any(w => w.Phase.Text == "Failed")) endMessage = "done, but " + string.Join("; ", ws.Where(w => w.Phase.Text == "Failed").Select(w => $"{w.ScopeId.Text} failed: {w.Message.Text}"));
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
            await Set(s => { s.Phase = endPhase; s.Message = endMessage; });
        }
    }

    /// <summary>One scope's worker: next spot for its frames, a dithered shot there, painted when it is done.</summary>
    private async Task WorkAsync(ImagingRequest r, Plan plan, ScopeFrames scope, CoveragePlanner planner, RemoteState<WeatherState>? weather, CancellationToken ct)
    {
        string id = scope.ScopeId;
        using var state = new RemoteState<ScopeState>(_node, ScopeIds.State(id), ScopeIds.GetState(id));
        await state.StartAsync();
        double dither = r.DitherArcsec.Value / 3600;
        int rejectedInARow = 0;
        while (!ct.IsCancellationRequested)
        {
            await WaitUntilAllowedAsync(weather, ct);
            Pose? pose;
            lock (_gate)
            {
                if (r.MaxVisits.Value > 0 && _visitsStarted >= r.MaxVisits.Value) pose = null;
                else
                {
                    // a new depth while running: this scope's plan is redone from what is still missing
                    if (planner.TargetSeconds != _target) { planner.TargetSeconds = _target; planner.Plan(); }
                    pose = planner.Next();
                    if (pose is not null) _visitsStarted++;
                }
            }
            if (pose is not { } planned) break;
            // unguided dithering: land anywhere within the dither radius of the planned spot
            double a = _random.NextDouble() * 2 * Math.PI, rr = dither * Math.Sqrt(_random.NextDouble());
            var at = planned with { X = planned.X + rr * Math.Cos(a), Y = planned.Y + rr * Math.Sin(a) };
            await SetWorker(id, w => { w.Phase = "Shooting"; w.PoseX = at.X; w.PoseY = at.Y; w.Message = ""; });
            string error = await ShootAsync(r, id, state, at, ct);
            double exp = r.Exposure.Seconds.Value, pa = r.PositionAngleDegrees.Value;
            if (error.StartsWith(RejectedMark))
            {
                // the scope judged the frame bad: it does not count; the spot goes back to the plan
                lock (_gate) { plan.Map.Paint(CoverageMap.Footprints(planned, scope.Frames, pa), -exp); _visitsStarted--; }
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
                lock (_gate) plan.Map.Paint(CoverageMap.Footprints(planned, scope.Frames, pa), -exp);
                await SetWorker(id, w => { w.Phase = "Failed"; w.Message = error; });
                return;
            }
            lock (_gate)
            {
                // the shot landed at the dithered spot: the plan learns where it really went, the real map gets it
                var real = CoverageMap.Footprints(at, scope.Frames, pa);
                plan.Map.Paint(CoverageMap.Footprints(planned, scope.Frames, pa), -exp);
                plan.Map.Paint(real, exp);
                plan.Actual.Paint(real, exp);
            }
            await SetWorker(id, w => w.Visits = w.Visits.Value + 1);
            await Set(s => s.Visits = s.Visits.Value + 1);
            Keep(r, plan);
        }
        if (!ct.IsCancellationRequested) await SetWorker(id, w => { w.Phase = "Done"; w.Message = ""; });
    }

    private const string RejectedMark = "!rejected:";

    /// <summary>One shot through the scope. "" when done and good; RejectedMark + why when every frame of it was rejected by
    /// the scope's grading; anything else is an error.</summary>
    private async Task<string> ShootAsync(ImagingRequest r, string scope, RemoteState<ScopeState> state, Pose at, CancellationToken ct)
    {
        var frames = new List<ShotEvent>();
        Action<ShotEvent> onShot = s => { lock (frames) frames.Add(s); };
        await _node.HookEventAsync(ShooterIds.Shot(scope), onShot, "image request: frame grades");
        try { return await ShootOnceAsync(r, scope, state, at, frames, ct); }
        finally { try { _node.UnhookEvent(ShooterIds.Shot(scope), onShot); } catch (ObjectDisposedException) { } }
    }

    private async Task<string> ShootOnceAsync(ImagingRequest r, string scope, RemoteState<ScopeState> state, Pose at, List<ShotEvent> frames, CancellationToken ct)
    {
        var (ra, dec) = PlanProjection.ToSky(r.Center.RaHours.Value, r.Center.DecDegrees.Value, r.PositionAngleDegrees.Value, at.X, at.Y);
        var start = await Commands.CallAsync(_node, ScopeIds.Command(scope, "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" }, Exposure = r.Exposure, Count = 1,
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

    private sealed record Kept(ImagingRequest Request, double Width, double Height, int Cols, int Rows, float[] Seconds, int Visits, string UpdatedUtc);
    private sealed record KeptMeta(double Width, double Height, int Cols, int Rows, int Visits, string UpdatedUtc);

    private static string Safe(string label) => new(label.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>After every good shot: the request, the real coverage and the count, written aside and moved in place.</summary>
    private void Keep(ImagingRequest r, Plan plan)
    {
        string? dir; int visits; float[] seconds;
        lock (_gate) { dir = _runDir; visits = _state.Visits.Value; seconds = (float[])plan.Actual.Seconds.Clone(); }
        if (dir is null) return;
        try
        {
            Directory.CreateDirectory(dir);
            void Write(string name, byte[] bytes) { string p = Path.Combine(dir, name); File.WriteAllBytes(p + ".part", bytes); File.Move(p + ".part", p, true); }
            Write("request.bin", r.ToBytes());
            Write("coverage.bin", System.Runtime.InteropServices.MemoryMarshal.AsBytes(seconds.AsSpan()).ToArray());
            Write("image.json", System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
                new KeptMeta(plan.Width, plan.Height, plan.Actual.Cols, plan.Actual.Rows, visits, DateTime.UtcNow.ToString("o")))));
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
            if (seconds.Length != meta.Cols * meta.Rows) return null;
            return new Kept(r, meta.Width, meta.Height, meta.Cols, meta.Rows, seconds, meta.Visits, meta.UpdatedUtc);
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
        lock (_gate) { if (_cts is null) return Task.FromResult(CommandResult.Fail("nothing is being imaged")); try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
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
                s.MinSeconds = map.Min(); s.MeanSeconds = map.Mean(); s.MaxSeconds = map.Max();
                var bytes = map.Render(Math.Max(_target, map.Max()), 96, 64, out int cols, out int rows);
                s.MapCols = cols; s.MapRows = rows; s.Map = new RawBytes(bytes);
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
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _commands.Dispose(); _publisher.Dispose();
    }
}
