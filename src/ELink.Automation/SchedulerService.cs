using System.Globalization;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Runs a schedule of image requests, night after night. At every check it takes the site's view of the sky
/// (dark? where is the Moon? what is up and until when?) and runs the best entry that is possible now and stays possible
/// for a while: higher priority first, then the one that sets soonest. An entry that stops being possible is stopped
/// (its image is kept and carries on later); one whose image reached its depth is complete. It drives only the image
/// request; everything per scope happens in the scopes.</summary>
public sealed class SchedulerService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string? _file;
    private readonly string _siteId;
    private readonly CommandSet _commands;
    private readonly StatePublisher<SchedulerState> _publisher;
    private readonly object _gate = new();
    private Schedule _schedule = new();
    private readonly Dictionary<string, ScheduleEntryState> _entryStates = new();
    private readonly Dictionary<string, DateTime> _failedAt = new();
    private readonly Dictionary<string, int> _failures = new();     // in a row, by entry: the wait grows with them
    // entries whose image ended Done, with the depth they were asked for: complete, whatever the kept coverage says (an image the
    // imaging service called done was started again at once, over and over, when its thinnest spot stayed short of the depth)
    private readonly Dictionary<string, double> _finished = new(StringComparer.OrdinalIgnoreCase);
    // entries whose image a person stopped while the queue ran it: left alone until the queue is started again or the entry is changed
    private readonly HashSet<string> _stoppedByPerson = new(StringComparer.OrdinalIgnoreCase);
    private string _phase = "Stopped", _running = "", _message = "";
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private RemoteState<SiteState>? _site;
    private RemoteState<ImagingState>? _imaging;

    /// <summary>How often the sky is looked at again.</summary>
    public TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>An entry whose image failed is left alone this long before it is tried again.</summary>
    public TimeSpan RetryFailedAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A first failure is often passing (devices still coming up after a restart): it is tried again soon, and only
    /// failures in a row back off to <see cref="RetryFailedAfter"/>.</summary>
    private TimeSpan WaitAfter(string label)
    {
        int n = _failures.TryGetValue(label, out var k) ? k : 1;
        double minutes = RetryFailedAfter.TotalMinutes;
        return TimeSpan.FromMinutes(n <= 1 ? Math.Min(2, minutes) : n == 2 ? Math.Min(10, minutes) : minutes);
    }

    public SchedulerService(TypeSafeEVentNode node, string? file = null, string siteId = SiteIds.Default)
    {
        _node = node; _file = file; _siteId = siteId;
        _commands = new CommandSet(node);
        _publisher = new(node, SchedulerIds.State, SchedulerIds.GetState, BuildState);
        if (file is not null && File.Exists(file))
        {
            try { Span<byte> bytes = File.ReadAllBytes(file); var s = new Schedule(); s.FromBytes(ref bytes); _schedule = s; }
            catch (Exception ex) { _message = $"could not read the schedule: {ex.Message}"; }
        }
    }

    public async Task StartAsync()
    {
        _site = new RemoteState<SiteState>(_node, SiteIds.State(_siteId), SiteIds.GetState(_siteId));
        await _site.StartAsync();
        _imaging = new RemoteState<ImagingState>(_node, ImagingIds.State, ImagingIds.GetState);
        await _imaging.StartAsync();
        await _commands.AddAsync<Schedule, CommandResult>(SchedulerIds.SetSchedule, SetScheduleAsync, "replace the schedule");
        await _commands.AddAsync<NOTESVoid, Schedule>(SchedulerIds.GetSchedule, _ => Task.FromResult(Copy()), "the schedule");
        await _commands.AddAsync<NOTESVoid, CommandResult>(SchedulerIds.Start, _ => StartRunAsync(), "run the schedule");
        await _commands.AddAsync<NOTESVoid, CommandResult>(SchedulerIds.Stop, async _ => { await StopRunAsync(); return CommandResult.Success(); }, "stop running the schedule");
        await _publisher.StartAsync();
        // it was running when the station stopped (a crash, a power cut, a restart): it runs again
        if (RunningFile is { } f && File.Exists(f))
            _ = Task.Run(async () =>
            {
                await Task.Delay(ResumeDelay);
                SessionLog.Info(_node, "queue", "the station stopped while the queue was running: running it again");
                await StartRunAsync();
            });
    }

    /// <summary>Waited before the queue runs again after a restart (the site and the imaging come up first).</summary>
    public TimeSpan ResumeDelay { get; set; } = TimeSpan.FromSeconds(3);
    private string? RunningFile => _file is null ? null : _file + ".running";

    private Schedule Copy()
    {
        lock (_gate) { Span<byte> b = _schedule.ToBytes(); var c = new Schedule(); c.FromBytes(ref b); return c; }
    }

    private async Task<CommandResult> SetScheduleAsync(Schedule s)
    {
        var labels = s.Entries.Select(e => e.Request.Label.Text.Trim()).ToList();
        if (labels.Any(l => l == "")) return CommandResult.Fail("every entry needs a name (its image's label)");
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count) return CommandResult.Fail("two entries have the same name");
        if (s.Entries.Any(e => e.Request.ScopeIds.Count == 0)) return CommandResult.Fail("every entry needs at least one scope");
        // what can never run is said now, not found out at the start (and tried again and again)
        foreach (var e in s.Entries)
            if (Problem(e) is { } bad) return CommandResult.Fail($"'{e.Request.Label.Text.Trim()}': {bad}");
        lock (_gate)
        {
            // a finished or hand-stopped entry stays so only while it is the same request
            var old = _schedule.Entries.ToDictionary(e => e.Request.Label.Text, e => RequestKey(e.Request), StringComparer.OrdinalIgnoreCase);
            var same = s.Entries.Where(e => old.TryGetValue(e.Request.Label.Text, out var b) && b == RequestKey(e.Request)).Select(e => e.Request.Label.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var l in _finished.Keys.Where(l => !same.Contains(l)).ToList()) _finished.Remove(l);
            _stoppedByPerson.RemoveWhere(l => !same.Contains(l));
            _schedule = s; _entryStates.Clear(); _failedAt.Clear();
        }
        if (_file is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!); File.WriteAllBytes(_file + ".part", s.ToBytes()); File.Move(_file + ".part", _file, true); }
            catch (Exception ex) { return CommandResult.Fail("could not keep the schedule: " + ex.Message); }
        }
        await CheckAsync(CancellationToken.None, decide: false);
        return CommandResult.Success();
    }

    private async Task<CommandResult> StartRunAsync()
    {
        lock (_gate)
        {
            _stoppedByPerson.Clear();     // starting the queue is the person taking those up again
            _parked = false;
            if (_run is not null) return CommandResult.Success();
            _run = new CancellationTokenSource();
            _phase = "Waiting"; _message = "";
            var ct = _run.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }
        try { if (RunningFile is { } f) File.WriteAllText(f, DateTime.UtcNow.ToString("o")); } catch { }
        await Publish();
        return CommandResult.Success();
    }

    private async Task StopRunAsync()
    {
        CancellationTokenSource? run; Task loop; string running;
        lock (_gate) { run = _run; _run = null; loop = _loop; running = _running; _running = ""; _phase = "Stopped"; }
        try { if (RunningFile is { } f) File.Delete(f); } catch { }
        if (run is null) return;
        run.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        if (running != "") await Commands.CallAsync(_node, ImagingIds.Abort, NOTESVoid.Void);
        await Publish();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct, decide: true); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { lock (_gate) _message = ex.Message; await Publish(); }
            // wake early when the image being taken ends
            var until = DateTime.UtcNow + CheckEvery;
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                string running; lock (_gate) running = _running;
                if (running != "" && _imaging?.Latest is { Phase.Text: "Done" or "Error" or "Aborted" }) break;
                await Task.Delay(250, ct);
            }
        }
    }

    // ---- deciding --------------------------------------------------------------------------------------------------

    private sealed record Verdict(ScheduleEntry Entry, string Status, string Reason, double Altitude, double HoursLeft, double Complete);

    /// <summary>Looks at every entry, and (when running) starts, keeps or stops the image.</summary>
    private async Task CheckAsync(CancellationToken ct, bool decide)
    {
        Schedule schedule; string running;
        lock (_gate) { schedule = _schedule; running = _running; }
        var site = _site?.Latest;
        if (decide)
        {
            var now = _imaging?.Latest;
            // the imaging service is about to carry on an image after a restart: that is what runs, not something the queue starts
            if (running == "" && now is { Phase.Text: "Resuming" })
            {
                lock (_gate) { _message = $"'{now.Label.Text}' carries on after the restart: the queue waits for it"; _phase = "Waiting"; }
                await Publish();
                return;
            }
            // an image being taken that this did not start (a restart carried it on, or a person started it): one of the queue's
            // is adopted; another is left to finish before the queue starts anything
            if (running == "" && now is { Phase.Text: "Running" or "Paused" or "WaitingForSky" or "WaitingForWeather" })
            {
                string label = now.Label.Text;
                if (schedule.Entries.Any(e => e.Request.Label.Text == label))
                {
                    lock (_gate) { _running = label; _failedAt.Remove(label); _message = $"{label}: carrying on"; }
                    running = label;
                }
                else
                {
                    lock (_gate) { _message = $"'{label}' is being taken (not from the queue): the queue waits for it to end"; _phase = "Waiting"; }
                    await Publish();
                    return;
                }
            }
        }
        var saved = await KeptAsync();
        var verdicts = new List<Verdict>();
        foreach (var e in schedule.Entries) verdicts.Add(await JudgeAsync(e, site, saved, running, schedule.MinRunMinutes.Value));

        if (decide)
        {
            var img = _imaging?.Latest;
            if (running != "")
            {
                var mine = verdicts.FirstOrDefault(v => v.Entry.Request.Label.Text == running);
                if (img is { Phase.Text: "Done" })
                {
                    // reached its depth (or its shot limit): the imaging says so, and that is final for this depth
                    double depth = schedule.Entries.FirstOrDefault(e => e.Request.Label.Text == running)?.Request.TargetSeconds.Value ?? 0;
                    lock (_gate) { _running = ""; _message = $"{running} finished"; _finished[running] = depth; }
                    string shortBy = saved.TryGetValue(running, out var k) && depth > 0 && k.MinSeconds.Value < depth - 1e-6
                        ? FormattableString.Invariant($" (its thinnest spot has {k.MinSeconds.Value:0.#} s of {depth:0.#} s)") : "";
                    SessionLog.Info(_node, "queue", $"'{running}' finished{shortBy}");
                    running = "";
                    saved = await KeptAsync();
                    verdicts = new List<Verdict>();
                    foreach (var e in schedule.Entries) verdicts.Add(await JudgeAsync(e, site, saved, "", schedule.MinRunMinutes.Value));
                }
                else if (img is { Phase.Text: "Aborted" })
                {
                    // the queue stops its own images itself (below): this one a person stopped, which means leave it, not try it again
                    lock (_gate) { _stoppedByPerson.Add(running); _running = ""; _message = $"{running} was stopped by hand: the queue leaves it (start the queue again to take it up)"; }
                    SessionLog.Info(_node, "queue", $"'{running}' was stopped by hand: the queue goes on without it");
                    running = "";
                    verdicts = new List<Verdict>();      // judged again: it was judged as the one running, which is never held
                    foreach (var e in schedule.Entries) verdicts.Add(await JudgeAsync(e, site, saved, "", schedule.MinRunMinutes.Value));
                }
                else if (img is { Phase.Text: "Error" })
                {
                    lock (_gate) { _failedAt[running] = DateTime.UtcNow; _failures[running] = (_failures.TryGetValue(running, out var fr) ? fr : 0) + 1; _running = ""; _message = $"{running}: {img.Message.Text}"; }
                    SessionLog.Warn(_node, "queue", $"'{running}' ended: {img.Phase.Text} ({img.Message.Text})");
                    running = "";
                }
                else if (mine is null || mine.Status is "Waiting" or "Disabled")
                {
                    // no longer possible (setting, dawn, the Moon): stop it; it is kept and carries on later
                    SessionLog.Info(_node, "queue", $"stopping '{running}': {mine?.Reason ?? "removed from the schedule"} (it is kept and carries on later)");
                    await Commands.CallAsync(_node, ImagingIds.Abort, NOTESVoid.Void);
                    await WaitImagingIdleAsync(ct);
                    lock (_gate) { _running = ""; _message = $"{running} stopped: {mine?.Reason ?? "removed from the schedule"}"; }
                    running = "";
                }
            }
            if (running == "")
            {
                var best = verdicts.Where(v => v.Status == "Possible")
                                   .OrderByDescending(v => v.Entry.Priority.Value).ThenBy(v => v.HoursLeft).FirstOrDefault();
                if (best is not null)
                {
                    var req = best.Entry.Request;
                    req.Resume = true;
                    var r = await Commands.CallAsync(_node, ImagingIds.Start, req, TimeSpan.FromMinutes(5));
                    lock (_gate)
                    {
                        if (r.Ok.Value) { _failures.Remove(req.Label.Text); _running = req.Label.Text; _message = $"{req.Label.Text}: {best.Reason}"; }
                        else { _failedAt[req.Label.Text] = DateTime.UtcNow; _failures[req.Label.Text] = (_failures.TryGetValue(req.Label.Text, out var fs) ? fs : 0) + 1; _message = $"{req.Label.Text} could not start: {r.Error.Text}"; }
                    }
                    if (r.Ok.Value) SessionLog.Info(_node, "queue", $"starting '{req.Label.Text}': {best.Reason}");
                    else SessionLog.Warn(_node, "queue", $"'{req.Label.Text}' could not start: {r.Error.Text}");
                }
                else lock (_gate) _message = verdicts.Count == 0 ? "the schedule is empty" : verdicts.All(v => v.Status is "Complete" or "Disabled") ? "everything is done" : "nothing can be taken now";
            }
            running = await EndOfNightAsync(verdicts, site, running);
        }
        lock (_gate)
        {
            _entryStates.Clear();
            foreach (var v in verdicts)
                _entryStates[v.Entry.Request.Label.Text] = new ScheduleEntryState
                {
                    Label = v.Entry.Request.Label.Text, Status = v.Entry.Request.Label.Text == _running ? "Running" : v.Status, Reason = v.Reason,
                    CompletePercent = Math.Round(v.Complete, 1), Altitude = v.Altitude,
                };
            if (_run is not null)
                _phase = _running != "" ? "Imaging" : verdicts.Count > 0 && verdicts.All(v => v.Status is "Complete" or "Disabled") ? "AllDone" : "Waiting";
        }
        await Publish();
    }

    // ---- the end of the night ----------------------------------------------------------------------------------------

    /// <summary>Park the mounts when the queue's night is over (default on): nobody is at the scope at dawn.</summary>
    public bool ParkWhenDone { get; set; } = true;
    public TimeSpan ParkTimeout { get; set; } = TimeSpan.FromMinutes(3);
    private bool _parked;
    private double _lastSunAltitude = double.NaN;

    /// <summary>Once the night's work is over, the mounts are parked rather than left tracking into the pier or the dawn: when
    /// everything in the queue is done, or in the morning (the Sun above -12° and rising) with nothing left running. An image
    /// still running when the Sun is up is stopped first (it is kept). A mount that cannot park (OnStep with no park position
    /// saved) has its tracking stopped instead. Should something become possible after all, its slew unparks the mount again.
    /// Returns what is running afterwards.</summary>
    private async Task<string> EndOfNightAsync(List<Verdict> verdicts, SiteState? site, string running)
    {
        double sun = site?.SunAltitude.Value ?? double.NaN;
        bool rising = !double.IsNaN(sun) && !double.IsNaN(_lastSunAltitude) && sun > _lastSunAltitude;
        if (!double.IsNaN(sun)) _lastSunAltitude = sun;
        if (running != "") { _parked = false; }
        if (!ParkWhenDone) return running;
        bool morning = rising && sun > -12;
        if (running != "" && morning && sun > 0)
        {
            SessionLog.Warn(_node, "queue", $"the Sun is up: stopping '{running}' (it is kept) and parking");
            await Commands.CallAsync(_node, ImagingIds.Abort, NOTESVoid.Void);
            await WaitImagingIdleAsync(CancellationToken.None);
            lock (_gate) { _running = ""; _message = $"{running} stopped: the Sun is up"; }
            running = "";
        }
        bool allDone = verdicts.Count > 0 && verdicts.All(v => v.Status is "Complete" or "Disabled");
        if (running != "" || _parked || !(allDone || morning)) return running;
        _parked = true;
        string why = allDone ? "everything in the queue is done" : "the night is over";
        List<string> mounts;
        try
        {
            var lists = await _node.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(5));
            mounts = (lists ?? []).SelectMany(l => l.Devices).Where(d => d.Kind.Text == DeviceKinds.Mount).Select(d => d.Id.Text).Distinct().ToList();
        }
        catch (Exception) { return running; }
        foreach (var m in mounts)
        {
            var park = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, m, "Park"), (BinaryConvertibleBool)true, TimeSpan.FromSeconds(30));
            if (park.Ok.Value)
            {
                // sent is not parked: a mount can take the command and then fail to get there
                MountState? st = null;
                for (var end = DateTime.UtcNow + ParkTimeout; DateTime.UtcNow < end; await Task.Delay(1000))
                {
                    st = (await _node.CallFunctionAsync<NOTESVoid, MountState>(EquipmentIds.GetState(DeviceKinds.Mount, m), NOTESVoid.Void, TimeSpan.FromSeconds(5)))?.FirstOrDefault();
                    if (st is null || st.Phase.Text is "Parked" or "Error") break;
                }
                if (st is { Phase.Text: "Parked" }) { SessionLog.Info(_node, "queue", $"{why}: {m} parked"); continue; }
                park = CommandResult.Fail(st is null ? "no answer" : st.Phase.Text == "Error" ? "the mount reported an error while parking" : $"not parked after {ParkTimeout.TotalMinutes:0} min");
            }
            var stop = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, m, "SetTracking"), (BinaryConvertibleBool)false, TimeSpan.FromSeconds(30));
            SessionLog.Warn(_node, "queue", $"{why}: {m} could not park ({park.Error.Text}){(stop.Ok.Value ? ": its tracking is stopped" : "")}");
        }
        lock (_gate) _message = $"{why}: mounts parked";
        return running;
    }

    private async Task WaitImagingIdleAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && _imaging?.Latest is { Phase.Text: "Running" or "Paused" or "WaitingForWeather" }) await Task.Delay(200, ct);
    }

    private async Task<Dictionary<string, SavedImage>> KeptAsync()
    {
        try
        {
            var answers = await _node.CallFunctionAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, NOTESVoid.Void, TimeSpan.FromSeconds(10));
            return (answers?.FirstOrDefault()?.Images ?? new()).ToDictionary(i => i.Label.Text, i => i, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return new(); }
    }

    private async Task<Verdict> JudgeAsync(ScheduleEntry e, SiteState? site, Dictionary<string, SavedImage> kept, string running, double minRunMinutes)
    {
        var r = e.Request;
        double complete = 0;
        if (kept.TryGetValue(r.Label.Text, out var k) && r.TargetSeconds.Value > 0) complete = Math.Min(100, 100 * k.MinSeconds.Value / r.TargetSeconds.Value);
        if (!e.Enabled.Value) return new(e, "Disabled", "disabled", double.NaN, 0, complete);
        if (r.TargetSeconds.Value > 0 && complete >= 100 - 1e-6) return new(e, "Complete", "deep enough", double.NaN, 0, complete);
        lock (_gate)
        {
            if ((r.TargetSeconds.Value > 0 || r.MaxVisits.Value > 0) && _finished.TryGetValue(r.Label.Text, out var depth) && Math.Abs(depth - r.TargetSeconds.Value) < 1e-9)
                return new(e, "Complete", "finished", double.NaN, 0, complete);
            if (_stoppedByPerson.Contains(r.Label.Text) && r.Label.Text != running) return new(e, "Waiting", "stopped by hand", double.NaN, 0, complete);
        }
        lock (_gate)
            if (_failedAt.TryGetValue(r.Label.Text, out var failed) && DateTime.UtcNow - failed < WaitAfter(r.Label.Text) && r.Label.Text != running)
                return new(e, "Waiting", "failed recently: tried again later", double.NaN, 0, complete);
        if (site is not { Known.Value: true }) return new(e, "Waiting", "the site is not set (Rig › Site)", double.NaN, 0, complete);
        var now = DateTime.Parse(site.UtcNow.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        if (Parse(e.NotBeforeUtc.Text) is { } nb && now < nb) return new(e, "Waiting", $"not before {nb:HH:mm} UTC", double.NaN, 0, complete);
        if (Parse(e.NotAfterUtc.Text) is { } na && now > na) return new(e, "Waiting", "its time window has passed", double.NaN, 0, complete);
        if (e.RequireDark.Value && site.Sky.Text != "Night") return new(e, "Waiting", $"not dark ({site.Sky.Text})", double.NaN, 0, complete);

        var answers = await _node.CallFunctionAsync<ObservabilityRequest, ObservabilityResult>(SiteIds.Observability(_siteId), new ObservabilityRequest
        {
            Target = new SkyTarget { RaHours = r.Center.RaHours.Value, DecDegrees = r.Center.DecDegrees.Value, Epoch = "J2000" }, MinAltitudeDegrees = e.MinAltitudeDegrees.Value, Hours = 24,
        }, TimeSpan.FromSeconds(20));
        if (answers?.FirstOrDefault() is not { Ok.Value: true } o) return new(e, "Waiting", "the site cannot say where it is", double.NaN, 0, complete);
        if (!o.AboveHorizon.Value) return new(e, "Waiting", o.RiseUtc.Text != "" ? $"too low, rises {Local(o.RiseUtc.Text)}" : "too low", o.Altitude.Value, 0, complete);
        bool moonUp = site.MoonAltitude.Value > 0;
        if (moonUp && e.MinMoonSeparationDegrees.Value > 0 && o.MoonSeparationDegrees.Value < e.MinMoonSeparationDegrees.Value)
            return new(e, "Waiting", $"{o.MoonSeparationDegrees.Value:0}° from the Moon", o.Altitude.Value, 0, complete);
        if (moonUp && site.MoonIllumination.Value > e.MaxMoonIllumination.Value)
            return new(e, "Waiting", $"the Moon is up and {site.MoonIllumination.Value * 100:0}% lit", o.Altitude.Value, 0, complete);

        // how long it stays possible: until it sets (or dawn, when it needs the dark)
        double hours = o.SetUtc.Text != "" ? (Parse(o.SetUtc.Text)!.Value - now).TotalHours : 24;
        if (e.RequireDark.Value && Parse(site.DawnUtc.Text) is { } dawn) hours = Math.Min(hours, (dawn - now).TotalHours);
        if (Parse(e.NotAfterUtc.Text) is { } end) hours = Math.Min(hours, (end - now).TotalHours);
        // the one already running keeps going until it really stops being possible
        if (r.Label.Text != running && hours * 60 < minRunMinutes) return new(e, "Waiting", $"only {hours * 60:0} min left tonight", o.Altitude.Value, hours, complete);
        return new(e, "Possible", $"up at {o.Altitude.Value:0}°, {hours:0.#} h left", o.Altitude.Value, hours, complete);
    }

    /// <summary>A request's identity: its bytes, with Resume as the queue starts it (it always carries an image on).</summary>
    private static string RequestKey(ImagingRequest r)
    {
        Span<byte> b = r.ToBytes(); var c = new ImagingRequest(); c.FromBytes(ref b); c.Resume = true;
        return Convert.ToBase64String(c.ToBytes());
    }

    /// <summary>Why an entry could never be taken, or null. The imaging checks the rest when it starts.</summary>
    private static string? Problem(ScheduleEntry e)
    {
        var r = e.Request;
        double ra = r.Center.RaHours.Value, dec = r.Center.DecDegrees.Value;
        if (double.IsNaN(ra) || ra < 0 || ra >= 24 || double.IsNaN(dec) || Math.Abs(dec) > 90) return "its centre is not on the sky";
        if (r.Center.Epoch.Text != "J2000") return "its centre must be J2000";
        if (!(r.Exposure.Seconds.Value > 0)) return "the exposure must be longer than zero";
        if (r.TargetSeconds.Value < 0 || double.IsNaN(r.TargetSeconds.Value)) return "the depth cannot be negative";
        DateTime? from = null, to = null;
        if (e.NotBeforeUtc.Text.Trim() != "" && (from = Parse(e.NotBeforeUtc.Text)) is null) return $"'{e.NotBeforeUtc.Text}' is not a time (not before)";
        if (e.NotAfterUtc.Text.Trim() != "" && (to = Parse(e.NotAfterUtc.Text)) is null) return $"'{e.NotAfterUtc.Text}' is not a time (not after)";
        if (from is { } f && to is { } t && t <= f) return "its window ends before it begins";
        if (e.MinAltitudeDegrees.Value > 90) return "its lowest altitude is above the zenith";
        return null;
    }

    private static DateTime? Parse(string iso) =>
        iso.Trim() != "" && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string Local(string iso) => Parse(iso) is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : iso;

    private async Task Publish() { try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { } }

    private SchedulerState BuildState()
    {
        lock (_gate)
        {
            var s = new SchedulerState { Phase = _phase, Running = _running, Message = _message };
            foreach (var e in _schedule.Entries)
                s.Entries.Add(_entryStates.TryGetValue(e.Request.Label.Text, out var st) ? st : new ScheduleEntryState { Label = e.Request.Label.Text, Status = "?" });
            return s;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? run; Task loop;
        lock (_gate) { run = _run; _run = null; loop = _loop; }
        run?.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _site?.Dispose(); _imaging?.Dispose();
        _commands.Dispose(); _publisher.Dispose();
    }
}
