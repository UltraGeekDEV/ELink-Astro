using System.Globalization;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>A smart scope: "point to, shoot at". It follows any number of Pointers and Shooters (only by their EVent
/// IDs) and presents itself as a Pointer and as a Shooter under its own id, so a scope can be a part of another scope.
/// <c>Observe</c> runs the whole job: point, wait until settled, take the requested exposures.</summary>
public sealed class SmartScope : IAsyncDisposable
{
    private sealed record PointerRef(string Id, RemoteState<PointerState> State);
    private sealed record ShooterRef(string Id, double East, double North, RemoteState<ShooterState> State, Action<ShotEvent> OnShot);

    private readonly TypeSafeEVentNode _node;
    private readonly ScopeDefinition _def;
    private readonly string _id;
    private readonly List<PointerRef> _pointers = new();
    private readonly List<ShooterRef> _shooters = new();
    private readonly CommandSet _commands;
    private readonly StatePublisher<PointerState> _pointerPub;
    private readonly StatePublisher<ShooterState> _shooterPub;
    private readonly StatePublisher<ScopeState> _scopePub;
    private readonly SemaphoreSlim _shots = new(0);
    private readonly object _scopeGate = new();
    private ScopeState _scope = new();
    private CancellationTokenSource? _observe;
    private Task? _observeTask;
    private (string Object, string Plan) _tag = ("", "");
    // the scope's own guiding: started when its pointers settle, stopped before they move, settled/dithered before exposures
    private readonly string _guiderId;
    private readonly RemoteState<GuiderState>? _guider;
    private readonly SemaphoreSlim _exposeLock = new(1, 1);
    private bool _guideRequested, _awaitSlew;
    private int _roundsSinceDither;
    private string _guideNote = "";
    // the scope's own meridian flip
    private readonly RemoteState<SiteState> _site;
    private SkyTarget? _target;            // the last target, J2000, for the flip's re-goto
    private bool _flippedForTarget;
    private double _haAtGoto = double.NaN;
    private Timer? _flipTimer;
    // the scope's own focus: one record per train with a focuser
    private sealed class FocusTrain
    {
        public required string TrainId, FocuserId, CameraShooter;
        public RemoteState<FocuserState>? Focuser;
        public DateTime? LastFocus;
        public double LastTemperature = double.NaN, BaselineHfr = double.NaN;
        public string LastFilter = "";
        public bool HasOffsets, PseudoMono;
        public readonly List<double> RecentHfr = new();
    }
    private List<RemoteState<TrainState>>? _trainStates;
    private bool _hasCooling;
    /// <summary>The longest a round waits for cameras to reach their cooling set points before it goes ahead anyway.</summary>
    public TimeSpan CoolWait { get; set; } = TimeSpan.FromMinutes(45);
    private List<FocusTrain>? _focusTrains;
    private volatile bool _preparing;
    // the scope's own frame grading: a baseline per camera, reset when it moves to another part of the sky
    private readonly Dictionary<string, ELink.Imaging.FrameGrader> _graders = new();
    private (double Ra, double Dec)? _gradedAt;   // centring and focus frames are the scope's own business: not relayed, not counted
    // the scope's own centring
    private bool _centred;
    private (double East, double North) _correction;      // degrees: where frames land relative to where it aims, learned from solves
    private (double Ra, double Dec)? _correctionAt;

    /// <param name="guiderId">the guider this scope drives; null = the definition's GuiderId</param>
    public SmartScope(TypeSafeEVentNode node, ScopeDefinition definition, string? guiderId = null)
    {
        _node = node; _def = definition; _id = definition.Id.Text;
        foreach (var p in definition.Pointers)
            _pointers.Add(new PointerRef(p.Text, new RemoteState<PointerState>(node, PointerIds.State(p.Text), PointerIds.GetState(p.Text))));
        foreach (var s in definition.Shooters)
        {
            string sid = s.Id.Text;
            _shooters.Add(new ShooterRef(sid, s.OffsetEastArcmin.Value, s.OffsetNorthArcmin.Value,
                new RemoteState<ShooterState>(node, ShooterIds.State(sid), ShooterIds.GetState(sid)), shot => _ = RelayShotAsync(shot)));
        }
        _commands = new CommandSet(node);
        _pointerPub = new(node, PointerIds.State(_id), PointerIds.GetState(_id), BuildPointer);
        _shooterPub = new(node, ShooterIds.State(_id), ShooterIds.GetState(_id), BuildShooter);
        _scopePub = new(node, ScopeIds.State(_id), ScopeIds.GetState(_id), () => { lock (_scopeGate) return Clone(_scope); });
        _guiderId = guiderId ?? definition.GuiderId.Text;
        string site = definition.SiteId.Text == "" ? SiteIds.Default : definition.SiteId.Text;
        _site = new RemoteState<SiteState>(node, SiteIds.State(site), SiteIds.GetState(site));
        if (_guiderId != "") _guider = new RemoteState<GuiderState>(node, GuiderIds.State(_guiderId), GuiderIds.GetState(_guiderId));
    }

    public string Id => _id;
    public ScopeDefinition Definition => _def;

    public async Task StartAsync()
    {
        if (_guider is not null) await _guider.StartAsync();
        await _site.StartAsync();
        foreach (var p in _pointers) { await p.State.StartAsync(); p.State.Changed += __ => { _ = _pointerPub.PublishAsync(); _ = Task.Run(RefreshScopeAsync); _ = Task.Run(FollowPointingAsync); }; }
        foreach (var s in _shooters)
        {
            await s.State.StartAsync();
            s.State.Changed += __ => { _ = _shooterPub.PublishAsync(); };
            await _node.HookEventAsync(ShooterIds.Shot(s.Id), s.OnShot, "shot relay");
        }

        await _commands.AddAsync<SkyTarget, CommandResult>(PointerIds.Goto(_id), GotoAsync, $"point every pointer of scope {_id} at a position");
        await _commands.AddAsync<NOTESVoid, CommandResult>(PointerIds.Abort(_id), _ => AbortPointersAsync(), "stop all pointers");
        await _commands.AddAsync<ShooterExposure, CommandResult>(ShooterIds.Expose(_id), ExposeCommandAsync, $"expose with every shooter of scope {_id} (guided: once settled, dithering as configured)");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ShooterIds.Abort(_id), _ => AbortShootersAsync(), "abort all exposures");
        await _commands.AddAsync<ObserveRequest, CommandResult>(ScopeIds.Command(_id, "Observe"), ObserveAsync, "point at a target, wait until settled, then take the exposures");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ScopeIds.Command(_id, "Abort"), _ => AbortAllAsync(), "stop the observation, all pointers and all shooters");

        await _pointerPub.StartAsync();
        await _shooterPub.StartAsync();
        await _scopePub.StartAsync();
        if (_def.MeridianFlip.Value) _flipTimer = new Timer(_ => _ = IdleFlipCheckAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // ---- Pointer interface ------------------------------------------------------------------------------------

    private Task<CommandResult> GotoAsync(SkyTarget target) => GotoAsync(target, flipping: false);

    /// <param name="flipping">a re-goto of the current target (flip, centring): keep the target and what is known about it</param>
    private async Task<CommandResult> GotoAsync(SkyTarget target, bool flipping)
    {
        if (_pointers.Count == 0) return CommandResult.Fail($"scope {_id} has no pointer");
        lock (_scopeGate) _centred = false;
        if (!flipping)
        {
            var (tra, tdec) = target.Epoch.Text == "JNow" ? Precession.DateToJ2000(target.RaHours.Value, target.DecDegrees.Value, DateTime.UtcNow) : (target.RaHours.Value, target.DecDegrees.Value);
            lock (_scopeGate) { _target = new SkyTarget { RaHours = tra, DecDegrees = tdec, Epoch = "J2000" }; _flippedForTarget = false; }
            _haAtGoto = HourAngle() ?? double.NaN;
        }
        lock (_scopeGate) _awaitSlew = true;   // ignore "on target" until the pointers have started to move
        await StopGuidingAsync();   // never guide through a slew
        double ra = target.RaHours.Value, dec = target.DecDegrees.Value;
        string epoch = target.Epoch.Text;
        // Put the primary shooter's centre, not the pointing axis, on the target.
        if (_shooters.Count > 0 && (_shooters[0].East != 0 || _shooters[0].North != 0))
            (ra, dec) = Sky.Offset(ra, dec, -_shooters[0].East, -_shooters[0].North);
        // aim off by the pointing correction learned nearby (a mount that lands a few arcminutes off, a guide-scope
        // misalignment, ...): the frames then land where they were asked to
        (double E, double N) corr; (double Ra, double Dec)? at;
        lock (_scopeGate) { corr = _correction; at = _correctionAt; }
        if (at is { } c && (corr.E != 0 || corr.N != 0))
        {
            var (j2Ra, j2Dec) = epoch == "JNow" ? Precession.DateToJ2000(ra, dec, DateTime.UtcNow) : (ra, dec);
            if (Sky.SeparationDegrees(j2Ra, j2Dec, c.Ra, c.Dec) < 15) (ra, dec) = Gnomonic.ToSky(ra, dec, -corr.E, -corr.N);
        }
        var adjusted = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = epoch };
        var results = await Task.WhenAll(_pointers.Select(p => Commands.CallAsync(_node, PointerIds.Goto(p.Id), adjusted)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task<CommandResult> AbortPointersAsync()
    {
        var results = await Task.WhenAll(_pointers.Select(p => Commands.CallAsync(_node, PointerIds.Abort(p.Id), NOTESVoid.Void)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private PointerState BuildPointer()
    {
        var states = _pointers.Select(p => p.State.Latest).ToList();
        var s = new PointerState();
        if (states.Count == 0 || states.Any(x => x is null) || states.Any(x => x!.Phase.Text == "Disconnected")) { s.Phase = "Disconnected"; return s; }
        var first = states[0]!;
        s.RaHours = first.RaHours; s.DecDegrees = first.DecDegrees;
        s.OnTarget = states.All(x => x!.OnTarget.Value);
        s.Phase = states.Any(x => x!.Phase.Text == "Error") ? "Error"
            : states.Any(x => x!.Phase.Text == "Slewing") ? "Slewing"
            : s.OnTarget ? "OnTarget"
            : states.Any(x => x!.Phase.Text == "Parked") ? "Parked" : "Idle";
        s.Message = states.Select(x => x!.Message.Text).FirstOrDefault(m => m != "") ?? "";
        s.PierSide = first.PierSide.Text;
        return s;
    }

    // ---- the scope's own meridian flip -------------------------------------------------------------------------

    /// <summary>Hour angle of the current target at the scope's site (hours, negative = east of the meridian); null
    /// when the site or the target is not known.</summary>
    private double? HourAngle()
    {
        SkyTarget? t; lock (_scopeGate) t = _target;
        if (t is null || _site.Latest is not { Known.Value: true } site) return null;
        var now = DateTime.UtcNow;
        var (ra, _) = Precession.J2000ToDate(t.RaHours.Value, t.DecDegrees.Value, now);
        return Horizon.HourAngleHours(ra, now, site.Config.LongitudeDegrees.Value);
    }

    /// <summary>Still on the side of the pier it took east of the meridian: West (looking east), or, for mounts that do
    /// not say, not flipped yet for a target that was east of the flip point when it was acquired.</summary>
    private bool OnPreFlipSide()
    {
        string pier = BuildPointer().PierSide.Text;
        if (pier == "West") return true;
        if (pier == "East") return false;
        bool flipped; lock (_scopeGate) flipped = _flippedForTarget;
        return !flipped && !double.IsNaN(_haAtGoto) && _haAtGoto < _def.FlipAfterHours.Value;
    }

    /// <summary>Before an exposure round: if the target will be past the flip point before the exposure ends, wait for
    /// the flip point (never flip mid-exposure, never too early for the mount to flip), then flip.</summary>
    private async Task<CommandResult> FlipIfDueAsync(double exposureSeconds, CancellationToken ct = default)
    {
        if (!_def.MeridianFlip.Value || HourAngle() is not { } ha || ha < -6 || !OnPreFlipSide()) return CommandResult.Success();
        double flipAt = _def.FlipAfterHours.Value;
        if (ha + exposureSeconds / 3600 < flipAt) return CommandResult.Success();
        if (ha < flipAt)
        {
            await SetScope(s => { s.Phase = "WaitingForFlip"; s.Message = FormattableString.Invariant($"meridian flip in {(flipAt - ha) * 60:0.0} min"); });
            while (HourAngle() is { } now && now < flipAt) await Task.Delay(500, ct);
        }
        return await FlipAsync(ct);
    }

    private async Task<CommandResult> FlipAsync(CancellationToken ct = default)
    {
        SkyTarget? target; lock (_scopeGate) target = _target;
        if (target is null) return CommandResult.Success();
        string before = BuildPointer().PierSide.Text;
        await SetScope(s => { s.Phase = "Flipping"; s.Message = "meridian flip"; });
        await StopGuidingAsync();
        var go = await GotoAsync(target, flipping: true);
        if (!go.Ok.Value) return CommandResult.Fail("meridian flip: " + go.Error.Text);
        // the pointer may still say "on target" for a moment: first see the mount move (or turn over), then settle
        var moving = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < moving && BuildPointer() is var p && p.Phase.Text != "Slewing" && (p.PierSide.Text == before || before == "Unknown")) await Task.Delay(200, ct);
        try
        {
            await Task.WhenAll(_pointers.Select(x => x.State.WaitAsync(s => s.OnTarget.Value || s.Phase.Text is "Error" or "Parked" or "Disconnected", TimeSpan.FromMinutes(10), ct)));
        }
        catch (TimeoutException) { return CommandResult.Fail("meridian flip: the mount did not settle"); }
        string after = BuildPointer().PierSide.Text;
        if (before == "West" && after == "West") return CommandResult.Fail("meridian flip: the mount did not turn over (check its meridian limits)");
        lock (_scopeGate) { _flippedForTarget = true; _correction = (0, 0); _correctionAt = null; }   // pointing errors change on the other side of the pier
        await SetScope(s => s.Message = $"meridian flip done (pier {after})");
        return CommandResult.Success();
    }

    // ---- the scope's own focus -----------------------------------------------------------------------------------

    /// <summary>The trains (among the scope's shooters) that have a focuser, each focused with its first imaging camera.</summary>
    private async Task<List<FocusTrain>> FocusTrainsAsync()
    {
        if (_focusTrains is not null) return _focusTrains;
        var list = new List<FocusTrain>();
        foreach (var sh in _shooters)
        {
            var answers = await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(sh.Id), NOTESVoid.Void, TimeSpan.FromSeconds(5));
            if (answers?.FirstOrDefault() is not { } t || t.FocuserId.Text == "") continue;
            var cam = t.Cameras.FirstOrDefault(c => c.Role.Text == "Imaging");
            if (cam is null) continue;
            var ft = new FocusTrain { TrainId = sh.Id, FocuserId = t.FocuserId.Text, CameraShooter = cam.ShooterId.Text, HasOffsets = t.FocusOffsets.Count > 0 || cam.PseudoMono.Value, PseudoMono = cam.PseudoMono.Value };
            ft.Focuser = new RemoteState<FocuserState>(_node, EquipmentIds.State(DeviceKinds.Focuser, ft.FocuserId), EquipmentIds.GetState(DeviceKinds.Focuser, ft.FocuserId));
            await ft.Focuser.StartAsync();
            list.Add(ft);
        }
        _focusTrains = list;
        return list;
    }

    private string? FocusReason(FocusTrain t, ShooterExposure e)
    {
        if (t.LastFocus is null) return _def.FocusOnStart.Value ? "first focus" : null;
        if (_def.RefocusEveryMinutes.Value > 0 && (DateTime.UtcNow - t.LastFocus.Value).TotalMinutes >= _def.RefocusEveryMinutes.Value) return "time";
        double temp = t.Focuser?.Latest?.Temperature.Value ?? double.NaN;
        if (_def.RefocusTemperatureDelta.Value > 0 && !double.IsNaN(temp) && !double.IsNaN(t.LastTemperature) && Math.Abs(temp - t.LastTemperature) >= _def.RefocusTemperatureDelta.Value)
            return FormattableString.Invariant($"temperature moved {temp - t.LastTemperature:+0.0;-0.0}°C");
        // a train with per-filter focus offsets moves its focuser itself when the filter changes
        if (_def.RefocusOnFilterChange.Value && !t.HasOffsets && e.Filter.Text != "" && !string.Equals(e.Filter.Text, t.LastFilter, StringComparison.OrdinalIgnoreCase)) return $"filter {e.Filter.Text}";
        lock (t.RecentHfr)
            if (_def.RefocusHfrIncreasePercent.Value > 0 && !double.IsNaN(t.BaselineHfr) && t.RecentHfr.Count >= 3)
            {
                double now = t.RecentHfr.Order().ElementAt(t.RecentHfr.Count / 2);
                if (now > t.BaselineHfr * (1 + _def.RefocusHfrIncreasePercent.Value / 100)) return FormattableString.Invariant($"stars grew {100 * (now / t.BaselineHfr - 1):0}%");
            }
        return null;
    }

    /// <summary>Before an exposure round: refocus every train whose trigger fired (all at once: each has its own focuser).</summary>
    private async Task FocusIfDueAsync(ShooterExposure e, CancellationToken ct = default)
    {
        if (!FocusTriggers) return;
        var trains = await FocusTrainsAsync();
        // without "focus at start", the first round is the baseline (you focused by hand): triggers count from here
        foreach (var t in trains.Where(t => t.LastFocus is null && !_def.FocusOnStart.Value))
        {
            t.LastFocus = DateTime.UtcNow; t.LastTemperature = t.Focuser?.Latest?.Temperature.Value ?? double.NaN; t.LastFilter = e.Filter.Text;
        }
        var due = trains.Select(t => (Train: t, Why: FocusReason(t, e))).Where(x => x.Why is not null).ToList();
        if (due.Count == 0) return;
        await SetScope(s => { s.Phase = "Focusing"; s.Message = string.Join(", ", due.Select(d => $"{d.Train.TrainId}: {d.Why}")); });
        var results = await Task.WhenAll(due.Select(async d =>
        {
            if (d.Train.PseudoMono) return (d.Train, State: await FocusColoursAsync(d.Train, ct));
            var r = await _node.CallFunctionAsync<AutofocusRequest, AutofocusState>(AutofocusIds.RunAndWait, new AutofocusRequest
            {
                ShooterId = d.Train.CameraShooter, FocuserId = d.Train.FocuserId, ExposureSeconds = _def.FocusExposureSeconds.Value,
                StepSize = _def.FocusStepSize.Value, Samples = _def.FocusSamples.Value, Filter = e.Filter.Text,
            }, TimeSpan.FromMinutes(30), ct);
            return (d.Train, State: r?.FirstOrDefault());
        }));
        var notes = new List<string>();
        foreach (var (t, st) in results)
        {
            // whatever the outcome, the trigger is re-armed from now (a failing focus is not retried every round)
            t.LastFocus = DateTime.UtcNow;
            t.LastTemperature = t.Focuser?.Latest?.Temperature.Value ?? double.NaN;
            t.LastFilter = e.Filter.Text;
            lock (t.RecentHfr) { t.RecentHfr.Clear(); t.BaselineHfr = double.NaN; }
            if (st is null) notes.Add($"{t.TrainId}: no autofocus service on the mesh");
            else if (st.Phase.Text != "Done") notes.Add($"{t.TrainId}: focus {st.Phase.Text.ToLowerInvariant()}: {st.Message.Text}");
        }
        if (notes.Count > 0) _guideNote = string.Join("; ", notes);
    }

    /// <summary>A pseudo mono camera is focused for each of its colours in turn (the reference, green, last): the focuser
    /// ends at green's best position and the train's focus offsets R and B become the differences measured.</summary>
    private async Task<AutofocusState?> FocusColoursAsync(FocusTrain t, CancellationToken ct)
    {
        var best = new Dictionary<string, int>();
        AutofocusState? last = null;
        foreach (string channel in new[] { "G", "R", "B" })
        {
            await SetScope(s => s.Message = $"{t.TrainId}: focusing {channel}");
            // every colour starts from green's focus: the colours differ by a little, and the sweep is centred where it starts
            if (best.TryGetValue("G", out int green) && t.Focuser is { } fz && fz.Latest?.Position.Value != green)
            {
                await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, t.FocuserId, "MoveTo"), (BinaryConvertibleInt32)green);
                try { await fz.WaitAsync(x => !x.Moving.Value && x.Position.Value == green, TimeSpan.FromSeconds(120), ct); } catch (TimeoutException) { }
            }
            var r = await _node.CallFunctionAsync<AutofocusRequest, AutofocusState>(AutofocusIds.RunAndWait, new AutofocusRequest
            {
                ShooterId = t.CameraShooter, FocuserId = t.FocuserId, ExposureSeconds = _def.FocusExposureSeconds.Value,
                StepSize = _def.FocusStepSize.Value, Samples = _def.FocusSamples.Value, Channel = channel,
            }, TimeSpan.FromMinutes(30), ct);
            last = r?.FirstOrDefault();
            if (last is null || last.Phase.Text != "Done") { if (last is not null) last.Message = $"{channel}: {last.Message.Text}"; return last; }
            best[channel] = last.BestPosition.Value;
        }
        // back to green (what the shooter takes as its reference), and the colours' places relative to it
        var offsets = new FocusOffsetList();
        foreach (var (c, p) in best) offsets.Offsets.Add(new FilterFocusOffset { Filter = c, Steps = p - best["G"] });
        var set = await Commands.CallAsync(_node, TrainIds.SetFocusOffsets(t.TrainId), offsets);
        if (!set.Ok.Value) { last.Phase = "Error"; last.Message = "could not keep the focus offsets: " + set.Error.Text; return last; }
        var home = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Focuser, t.FocuserId, "MoveTo"), (BinaryConvertibleInt32)best["G"]);
        if (!home.Ok.Value) { last.Phase = "Error"; last.Message = "could not return to green focus: " + home.Error.Text; return last; }
        if (t.Focuser is { } f) { try { await f.WaitAsync(x => !x.Moving.Value && x.Position.Value == best["G"], TimeSpan.FromSeconds(120), ct); } catch (TimeoutException) { } }
        await SetScope(s => s.Message = $"{t.TrainId}: focus offsets R {offsets.Offsets.First(o => o.Filter.Text == "R").Steps.Value:+0;-0;0}, B {offsets.Offsets.First(o => o.Filter.Text == "B").Steps.Value:+0;-0;0} steps from green");
        return last;
    }

    private sealed record Grade(string Quality, string Note, int Stars, double Hfr, double Elongation, double Background);

    /// <summary>Measures a Light frame and grades it against this camera's recent good frames (see FrameGrader).
    /// Frames already graded by a child scope, non-FITS frames and calibration frames pass as they are.</summary>
    private async Task<Grade> GradeAsync(ShotEvent shot)
    {
        var none = new Grade(shot.Quality.Text, shot.QualityNote.Text, shot.Stars.Value, shot.Hfr.Value, shot.Elongation.Value, shot.Background.Value);
        if (!_def.GradeFrames.Value || shot.Quality.Text != "" || shot.FrameType.Text is not ("Light" or "") || !shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase)) return none;
        ELink.Imaging.FrameMetrics m;
        try { m = await Task.Run(() => ELink.Imaging.FrameMetrics.Measure(ELink.Imaging.FitsImage.Parse(shot.Data.Data))); }
        catch (Exception) { return none; }
        ELink.Imaging.FrameGrader grader;
        lock (_scopeGate)
        {
            // another part of the sky has other stars and another sky: start the baseline again
            if (_target is { } t && (_gradedAt is not { } at || Sky.SeparationDegrees(at.Ra, at.Dec, t.RaHours.Value, t.DecDegrees.Value) > 1))
            {
                foreach (var g in _graders.Values) g.Reset();
                _gradedAt = (t.RaHours.Value, t.DecDegrees.Value);
            }
            if (!_graders.TryGetValue(shot.Shooter.Text, out grader!)) _graders[shot.Shooter.Text] = grader = new ELink.Imaging.FrameGrader();
        }
        (bool ok, string why) result;
        lock (grader) result = grader.Grade(m);
        return new Grade(result.ok ? "Good" : "Rejected", result.why, m.Stars, m.Hfr, m.Elongation, m.Background);
    }

    /// <summary>Star size of the frames a focusing train takes, for the HFR trigger: the first frame after a focus is the
    /// baseline, the last three the current size.</summary>
    private void WatchHfr(ShotEvent shot)
    {
        if (!(_def.RefocusHfrIncreasePercent.Value > 0) || _focusTrains is not { } trains) return;
        var t = trains.FirstOrDefault(x => x.CameraShooter == shot.Shooter.Text);
        if (t is null || !shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase) || shot.FrameType.Text is not ("Light" or "")) return;
        var data = shot.Data.Data;
        _ = Task.Run(() =>
        {
            try
            {
                var r = ELink.Imaging.FrameMetrics.Measure(ELink.Imaging.FitsImage.Parse(data));
                if (r.Stars < 3 || double.IsNaN(r.Hfr)) return;
                lock (t.RecentHfr)
                {
                    if (double.IsNaN(t.BaselineHfr)) { t.BaselineHfr = r.Hfr; return; }
                    t.RecentHfr.Add(r.Hfr); if (t.RecentHfr.Count > 3) t.RecentHfr.RemoveAt(0);
                }
            }
            catch (Exception) { }
        });
    }

    // ---- the scope's own centring ------------------------------------------------------------------------------

    /// <summary>After a slew: solve a frame, and while it is off by more than the tolerance re-aim by the error (no sync
    /// needed: it works on mounts that ignore syncs, and corrects guide-scope or flexure offsets too). What it learns is
    /// applied to later slews nearby. A failed solve is noted, not fatal: the round goes ahead.</summary>
    private async Task CentreAsync(CancellationToken ct = default)
    {
        if (!_def.CenterAfterSlew.Value || _shooters.Count == 0) return;
        SkyTarget? target; bool centred;
        lock (_scopeGate) { target = _target; centred = _centred; }
        if (target is null || centred) return;
        string shooter = _def.CenterShooterId.Text != "" ? _def.CenterShooterId.Text : _shooters[0].Id;
        // where that shooter's centre should be: the target, moved by its offset from the primary shooter
        var sref = _shooters.FirstOrDefault(s => s.Id == shooter);
        double offE = sref is null ? 0 : (sref.East - _shooters[0].East) / 60, offN = sref is null ? 0 : (sref.North - _shooters[0].North) / 60;
        var (wantRa, wantDec) = Gnomonic.ToSky(target.RaHours.Value, target.DecDegrees.Value, offE, offN);
        double tol = Math.Max(0.05, _def.CenterToleranceArcmin.Value);
        await StopGuidingAsync();
        (double Ra, double Dec)? lastSolved = null;
        double lastMove = 0;   // degrees the last re-aim moved the pointing
        for (int attempt = 1; attempt <= Math.Max(1, _def.CenterMaxTries.Value); attempt++)
        {
            await SetScope(s => { s.Phase = "Centering"; s.Message = attempt == 1 ? "plate solving" : $"plate solving (try {attempt})"; });
            var answers = await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
            {
                ShooterId = shooter, ExposureSeconds = _def.CenterExposureSeconds.Value, HintRaHours = wantRa, HintDecDegrees = wantDec, HintRadiusDegrees = 5, TimeoutSeconds = 90,
            }, TimeSpan.FromSeconds(_def.CenterExposureSeconds.Value + 200), ct);
            var solved = answers?.FirstOrDefault();
            if (solved is not { Solved.Value: true })
            {
                _guideNote = "centring: " + (solved is null ? "no plate solver on the mesh" : "no solution: " + solved.Message.Text);
                return;
            }
            var (e, n) = Gnomonic.FromSky(wantRa, wantDec, solved.RaHours.Value, solved.DecDegrees.Value);
            double err = Math.Sqrt(e * e + n * n) * 60;
            // a re-aim that did not move the field: the frame was taken before the move showed; look again rather than
            // correcting twice for the same error
            if (lastSolved is { } prev && lastMove * 60 > tol && Sky.SeparationDegrees(prev.Ra, prev.Dec, solved.RaHours.Value, solved.DecDegrees.Value) < 0.3 * lastMove)
            {
                lastMove = 0;
                await SetScope(s => s.Message = "the field has not moved yet: solving again");
                await Task.Delay(2000, ct);
                continue;
            }
            lastSolved = (solved.RaHours.Value, solved.DecDegrees.Value);
            if (err <= tol)
            {
                lock (_scopeGate) _centred = true;
                await SetScope(s => s.Message = FormattableString.Invariant($"centred to {err:0.00}'"));
                return;
            }
            // frames land (e, n) from where they should: aim that much the other way, and remember it
            lock (_scopeGate)
            {
                // add to the correction this slew used; one learned far from here was not used, so start afresh
                bool used = _correctionAt is { } at && Sky.SeparationDegrees(at.Ra, at.Dec, target.RaHours.Value, target.DecDegrees.Value) < 15;
                _correction = used ? (_correction.East + e, _correction.North + n) : (e, n);
                _correctionAt = (target.RaHours.Value, target.DecDegrees.Value);
            }
            lastMove = Math.Sqrt(e * e + n * n);
            await SetScope(s => s.Message = FormattableString.Invariant($"off by {err:0.0}', re-aiming"));
            var go = await GotoAsync(target, flipping: true);
            if (!go.Ok.Value) { _guideNote = "centring: " + go.Error.Text; return; }
            await WaitSettledAfterGotoAsync(ct);
        }
        _guideNote = FormattableString.Invariant($"centring: still off after {_def.CenterMaxTries.Value} tries");
    }

    /// <summary>After a re-goto of the same spot: let the pointers start moving (they may still say "on target" for a
    /// moment), then wait until they settle.</summary>
    private async Task WaitSettledAfterGotoAsync(CancellationToken ct)
    {
        var moving = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < moving && BuildPointer().Phase.Text != "Slewing") await Task.Delay(100, ct);
        try
        {
            await Task.WhenAll(_pointers.Select(x => x.State.WaitAsync(s => s.OnTarget.Value || s.Phase.Text is "Error" or "Parked" or "Disconnected", TimeSpan.FromMinutes(5), ct)));
        }
        catch (TimeoutException) { }
        await Task.Delay(2500, ct);   // let the mount settle and the camera's view catch up
    }

    /// <summary>Tracking on target with nothing to expose: flip when due rather than track into the pier.</summary>
    private async Task IdleFlipCheckAsync()
    {
        if (!_exposeLock.Wait(0)) return;   // a round is being prepared or exposed: it checks by itself
        try
        {
            if (BuildPointer().Phase.Text != "OnTarget" || HourAngle() is not { } ha || ha < _def.FlipAfterHours.Value || ha > 6 || !OnPreFlipSide()) return;
            var r = await FlipAsync();
            if (!r.Ok.Value) await SetScope(s => s.Message = r.Error.Text);
        }
        catch (Exception ex) { try { await SetScope(s => s.Message = "meridian flip: " + ex.Message); } catch { } }
        finally { _exposeLock.Release(); }
    }

    // ---- the scope's own guiding --------------------------------------------------------------------------------

    private async Task StopGuidingAsync()
    {
        if (_guider is null) return;
        lock (_scopeGate) _guideRequested = false;
        await Commands.CallAsync(_node, GuiderIds.Stop(_guiderId), NOTESVoid.Void, TimeSpan.FromSeconds(60));
    }

    /// <summary>Whoever moved the pointers (this scope, a parent scope, a hand controller): guide once they have settled,
    /// stop when they move.</summary>
    private async Task FollowPointingAsync()
    {
        if (_guider is null) return;
        var p = BuildPointer();
        bool start = false, stop = false;
        lock (_scopeGate)
        {
            if (p.Phase.Text != "OnTarget") _awaitSlew = false;
            // with centring on, guiding starts after it (the exposure round does both, in order)
            if (p.Phase.Text == "OnTarget" && !_guideRequested && !_awaitSlew && (!_def.CenterAfterSlew.Value || _centred)) { _guideRequested = true; _roundsSinceDither = 0; start = true; }
            else if (p.Phase.Text is "Slewing" or "Parked" or "Disconnected" && _guideRequested) { _guideRequested = false; stop = true; }
        }
        if (start) await Commands.CallAsync(_node, GuiderIds.Start(_guiderId), new GuideStartRequest());
        if (stop) await Commands.CallAsync(_node, GuiderIds.Stop(_guiderId), NOTESVoid.Void, TimeSpan.FromSeconds(60));
    }

    /// <summary>Before an exposure round: make sure the guider runs, then dither (every DitherEvery rounds) or just wait
    /// until settled. A guider that fails to start fails the round; one that is merely slow to settle is noted and the
    /// round goes ahead.</summary>
    private bool Prepared => _hasCooling || _guider is not null || _def.MeridianFlip.Value || _def.CenterAfterSlew.Value || FocusTriggers;
    private bool FocusTriggers => _def.FocusOnStart.Value || _def.RefocusEveryMinutes.Value > 0 || _def.RefocusTemperatureDelta.Value > 0
                                  || _def.RefocusOnFilterChange.Value || _def.RefocusHfrIncreasePercent.Value > 0;

    /// <summary>Finds the scope's trains (once) and whether any of their cameras is cooled.</summary>
    private async Task LearnTrainsAsync()
    {
        if (_trainStates is not null) return;
        var list = new List<RemoteState<TrainState>>();
        foreach (var sh in _shooters)
        {
            var answers = await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(sh.Id), NOTESVoid.Void, TimeSpan.FromSeconds(5));
            if (answers?.FirstOrDefault() is null) continue;
            var rs = new RemoteState<TrainState>(_node, TrainIds.State(sh.Id), TrainIds.GetState(sh.Id));
            await rs.StartAsync();
            list.Add(rs);
        }
        _hasCooling = list.Any(t => t.Latest?.Cameras.Any(c => c.Cooler.Text != "") == true);
        _trainStates = list;
    }

    /// <summary>Before a round: cameras still cooling down are waited for (up to CoolWait), so frames match their darks.</summary>
    private async Task WaitColdAsync(CancellationToken ct = default)
    {
        if (!_hasCooling || _trainStates is null) return;
        var until = DateTime.UtcNow + CoolWait;
        while (true)
        {
            var cooling = _trainStates.SelectMany(t => t.Latest?.Cameras.Where(c => c.Cooler.Text == "Cooling") ?? []).ToList();
            if (cooling.Count == 0) return;
            if (DateTime.UtcNow > until) { _guideNote = "went ahead before the cameras were cold"; return; }
            await SetScope(s => { s.Phase = "Cooling"; s.Message = string.Join(", ", cooling.Select(c => FormattableString.Invariant($"{c.CameraId.Text} {c.Temperature.Value:0.0} °C (set point {c.CoolerSetPoint.Value:0.0})"))); });
            await Task.Delay(1000, ct);
        }
    }

    private async Task<CommandResult> PrepareRoundAsync(ShooterExposure exposure)
    {
        _preparing = true;
        try
        {
            await WaitColdAsync();
            var flip = await FlipIfDueAsync(exposure.Seconds.Value);
            if (!flip.Ok.Value) return flip;
            await CentreAsync();
            await FocusIfDueAsync(exposure);
        }
        finally { _preparing = false; }
        if (_guider is null) return CommandResult.Success();
        // starting is idempotent: a running guider just says yes
        lock (_scopeGate) _guideRequested = true;
        var started = await Commands.CallAsync(_node, GuiderIds.Start(_guiderId), new GuideStartRequest());
        if (!started.Ok.Value) return CommandResult.Fail("guiding: " + started.Error.Text);
        bool dither;
        lock (_scopeGate) dither = _def.DitherEvery.Value > 0 && _roundsSinceDither >= _def.DitherEvery.Value;
        await SetScope(s => s.Phase = dither ? "Dithering" : "Guiding");
        double timeout = Math.Max(10, _def.SettleTimeoutSeconds.Value);
        var r = await Commands.CallAsync(_node, GuiderIds.Dither(_guiderId), new DitherRequest
        {
            Pixels = dither ? _def.DitherPixels.Value : 0, SettlePixels = _def.SettlePixels.Value, SettleSeconds = _def.SettleSeconds.Value, TimeoutSeconds = timeout,
        }, TimeSpan.FromSeconds(timeout + 30));
        if (dither) lock (_scopeGate) _roundsSinceDither = 0;
        if (r.Ok.Value) return CommandResult.Success();
        if (_guider.Latest is { Phase.Text: "Guiding" }) { _guideNote = r.Error.Text; return CommandResult.Success(); }
        return CommandResult.Fail("guiding: " + r.Error.Text);
    }

    // ---- Shooter interface ------------------------------------------------------------------------------------

    /// <summary>The Expose command: unguided scopes expose at once; guided ones answer "accepted" and expose once
    /// guiding has settled (the frames, as always, arrive as shot events).</summary>
    private async Task<CommandResult> ExposeCommandAsync(ShooterExposure e)
    {
        if (_shooters.Count == 0) return CommandResult.Fail($"scope {_id} has no shooter");
        await LearnTrainsAsync();
        if (!Prepared) return await ExposeAsync(e);
        _ = Task.Run(async () =>
        {
            var r = await ExposePreparedAsync(e);
            if (!r.Ok.Value) await SetScope(s => s.Message = r.Error.Text);
        });
        return CommandResult.Success();
    }

    private async Task<CommandResult> ExposePreparedAsync(ShooterExposure e)
    {
        await _exposeLock.WaitAsync();
        try
        {
            var g = await PrepareRoundAsync(e);
            if (!g.Ok.Value) return g;
            while (_shots.CurrentCount > 0) _shots.Wait(0);   // count only this round's frames
            var r = await ExposeAsync(e);
            if (r.Ok.Value) lock (_scopeGate) _roundsSinceDither++;
            return r;
        }
        finally { _exposeLock.Release(); }
    }

    private async Task<CommandResult> ExposeAsync(ShooterExposure e)
    {
        if (_shooters.Count == 0) return CommandResult.Fail($"scope {_id} has no shooter");
        var results = await Task.WhenAll(_shooters.Select(s => Commands.CallAsync(_node, ShooterIds.Expose(s.Id), e)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private async Task<CommandResult> AbortShootersAsync()
    {
        var results = await Task.WhenAll(_shooters.Select(s => Commands.CallAsync(_node, ShooterIds.Abort(s.Id), NOTESVoid.Void)));
        return results.FirstOrDefault(r => !r.Ok.Value) ?? CommandResult.Success();
    }

    private ShooterState BuildShooter()
    {
        var states = _shooters.Select(s => s.State.Latest).ToList();
        var s = new ShooterState();
        if (states.Count == 0 || states.All(x => x is null || x.Phase.Text == "Disconnected")) { s.Phase = "Disconnected"; return s; }
        var live = states.Where(x => x is not null && x.Phase.Text != "Disconnected").Select(x => x!).ToList();
        s.Phase = live.Any(x => x.Phase.Text == "Error") ? "Error" : live.Any(x => x.Phase.Text == "Exposing") ? "Exposing" : "Idle";
        s.ExposureRemaining = live.Max(x => x.ExposureRemaining.Value);
        s.Filter = live.Select(x => x.Filter.Text).FirstOrDefault(f => f != "") ?? "";
        s.ShotsPerExposure = states.Sum(x => x?.ShotsPerExposure.Value ?? 1);
        s.Message = live.Select(x => x.Message.Text).FirstOrDefault(m => m != "") ?? "";
        return s;
    }

    /// <summary>A child shooter delivered a frame: note it for a running Observe and pass it on as our own, with pointing.</summary>
    private async Task RelayShotAsync(ShotEvent shot)
    {
        if (_preparing) return;
        WatchHfr(shot);
        // Read the run's tags first: once the round is counted the run may end, and the tags with it.
        (string obj, string plan) tag;
        lock (_scopeGate) tag = _tag;
        var p = _pointers.Count > 0 ? _pointers[0].State.Latest : null;
        var grade = await GradeAsync(shot);
        var relayed = new ShotEvent
        {
            Quality = grade.Quality, QualityNote = grade.Note, Stars = grade.Stars, Hfr = grade.Hfr, Elongation = grade.Elongation, Background = grade.Background,
            Shooter = shot.Shooter, Format = shot.Format, ExposureSeconds = shot.ExposureSeconds, FrameType = shot.FrameType,
            Filter = shot.Filter, Timestamp = shot.Timestamp, Data = shot.Data, PseudoChannel = shot.PseudoChannel, PixelScaleArcsec = shot.PixelScaleArcsec, CameraAngleDegrees = shot.CameraAngleDegrees,
            ObjectName = shot.ObjectName.Text != "" ? shot.ObjectName.Text : tag.obj,
            PlanId = shot.PlanId.Text != "" ? shot.PlanId.Text : tag.plan,
            PointingRaHours = double.IsNaN(shot.PointingRaHours.Value) ? (p?.RaHours.Value ?? double.NaN) : shot.PointingRaHours.Value,
            PointingDecDegrees = double.IsNaN(shot.PointingDecDegrees.Value) ? (p?.DecDegrees.Value ?? double.NaN) : shot.PointingDecDegrees.Value,
        };
        // fire first, count second: whoever waits for the round to end must already have the frame (and its grade)
        try { await _node.FireEventAsync(ShooterIds.Shot(_id), relayed); } catch (ObjectDisposedException) { }
        _shots.Release();
    }

    // ---- Scope: the whole job --------------------------------------------------------------------------------

    private Task<CommandResult> AbortAllAsync()
    {
        return Task.Run(async () =>
        {
            try { _observe?.Cancel(); } catch (ObjectDisposedException) { }
            var a = await AbortPointersAsync();
            var b = await AbortShootersAsync();
            return !a.Ok.Value ? a : b;
        });
    }

    private async Task<CommandResult> ObserveAsync(ObserveRequest r)
    {
        var rejected = StartObserve(r);
        if (rejected is not null) return rejected;
        // Publish "observing" before answering: whoever waits for the run to end must not see the previous run's state.
        await _scopePub.PublishAsync();
        return CommandResult.Success();   // accepted; progress arrives as scope state events
    }

    private CommandResult? StartObserve(ObserveRequest r)
    {
        if (r.Count.Value < 1) return CommandResult.Fail("Count must be at least 1");
        if (_pointers.Count == 0) return CommandResult.Fail($"scope {_id} has no pointer");
        if (_shooters.Count == 0) return CommandResult.Fail($"scope {_id} has no shooter");
        lock (_scopeGate)
        {
            if (_observe is not null) return CommandResult.Fail("already observing");
            _observe = new CancellationTokenSource();
            _tag = (r.ObjectName.Text, r.PlanId.Text);
            _scope = new ScopeState { Phase = "Pointing", Observing = true, ShotsPlanned = r.Count.Value, ShotsDone = 0 };
            _guideNote = "";
        }
        var cts = _observe;
        _observeTask = Task.Run(() => RunObserveAsync(r, cts!));
        return null;
    }

    private async Task RunObserveAsync(ObserveRequest r, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string endPhase = "Idle", endMessage = "";
        try
        {
            await SetScope(s => { s.Phase = "Pointing"; s.Message = ""; });
            var go = await GotoAsync(r.Target);
            if (!go.Ok.Value) throw new InvalidOperationException(go.Error.Text);
            var slewTimeout = TimeSpan.FromSeconds(Math.Max(10, r.SlewTimeoutSeconds.Value));
            // wait for every pointer to settle, but give up early when one cannot (parked, failed, gone)
            var settled = await Task.WhenAll(_pointers.Select(p => p.State.WaitAsync(
                s => s.OnTarget.Value || s.Phase.Text is "Error" or "Parked" or "Disconnected", slewTimeout, ct)));
            var failed = settled.FirstOrDefault(s => !s.OnTarget.Value);
            if (failed is not null)
                throw new InvalidOperationException($"a pointer cannot reach the target: {failed.Phase.Text}" + (failed.Message.Text != "" ? $" ({failed.Message.Text})" : ""));
            await SetScope(s => s.Phase = "OnTarget");

            int perRound = Math.Max(1, _shooters.Sum(s => s.State.Latest?.ShotsPerExposure.Value ?? 1));
            await LearnTrainsAsync();
            for (int i = 0; i < r.Count.Value; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (_shots.CurrentCount > 0) _shots.Wait(0); // forget frames from before this round
                var ex = !Prepared ? await ExposeAsync(r.Exposure) : await ExposePreparedAsync(r.Exposure);
                if (!ex.Ok.Value) throw new InvalidOperationException(ex.Error.Text);
                await SetScope(s => { s.Phase = "Exposing"; s.Message = _guideNote; });
                var roundTimeout = TimeSpan.FromSeconds(r.Exposure.Seconds.Value + 180);
                for (int k = 0; k < perRound; k++)
                    if (!await _shots.WaitAsync(roundTimeout, ct)) throw new TimeoutException("a frame did not arrive in time");
                int done = i + 1;
                await SetScope(s => s.ShotsDone = done);
            }
            await SetScope(s => s.Phase = "OnTarget");
        }
        catch (OperationCanceledException) { endMessage = "aborted"; }
        catch (TimeoutException ex) { endPhase = "Error"; endMessage = ex.Message; }
        catch (Exception ex) { endPhase = "Error"; endMessage = ex.Message; }
        finally
        {
            lock (_scopeGate) { _observe = null; _tag = ("", ""); }
            cts.Dispose();
            string note = endMessage != "" ? endMessage : _guideNote;   // a guiding warning outlives a successful run
            await SetScope(s => { s.Observing = false; s.Phase = endPhase; s.Message = note; });
        }
    }

    private async Task SetScope(Action<ScopeState> change)
    {
        lock (_scopeGate) change(_scope);
        await _scopePub.PublishAsync();
    }

    private async Task RefreshScopeAsync() => await _scopePub.PublishAsync();

    private static ScopeState Clone(ScopeState s) => new()
    {
        Phase = s.Phase.Text, Message = s.Message.Text, ShotsDone = s.ShotsDone.Value, ShotsPlanned = s.ShotsPlanned.Value, Observing = s.Observing.Value,
    };

    public async ValueTask DisposeAsync()
    {
        try { _observe?.Cancel(); } catch (ObjectDisposedException) { }
        if (_observeTask is not null) await Task.WhenAny(_observeTask, Task.Delay(2000));
        _commands.Dispose(); _pointerPub.Dispose(); _shooterPub.Dispose(); _scopePub.Dispose();
        _guider?.Dispose(); _site.Dispose();
        foreach (var t in _focusTrains ?? []) t.Focuser?.Dispose();
        foreach (var t in _trainStates ?? []) t.Dispose();
        if (_flipTimer is not null) await _flipTimer.DisposeAsync();
        foreach (var p in _pointers) p.State.Dispose();
        foreach (var s in _shooters)
        {
            s.State.Dispose();
            try { _node.UnhookEvent(ShooterIds.Shot(s.Id), s.OnShot); } catch (ObjectDisposedException) { }
        }
    }
}
