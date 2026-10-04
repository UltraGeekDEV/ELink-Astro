using System.Globalization;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

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
    private string _phase = "Stopped", _running = "", _message = "";
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private RemoteState<SiteState>? _site;
    private RemoteState<ImagingState>? _imaging;

    /// <summary>How often the sky is looked at again.</summary>
    public TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>An entry whose image failed is left alone this long before it is tried again.</summary>
    public TimeSpan RetryFailedAfter { get; set; } = TimeSpan.FromMinutes(30);

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
    }

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
        lock (_gate) { _schedule = s; _entryStates.Clear(); _failedAt.Clear(); }
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
            if (_run is not null) return CommandResult.Success();
            _run = new CancellationTokenSource();
            _phase = "Waiting"; _message = "";
            var ct = _run.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }
        await Publish();
        return CommandResult.Success();
    }

    private async Task StopRunAsync()
    {
        CancellationTokenSource? run; Task loop; string running;
        lock (_gate) { run = _run; _run = null; loop = _loop; running = _running; _running = ""; _phase = "Stopped"; }
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
                    // reached its depth (or its shot limit): complete once the kept coverage says so
                    lock (_gate) { _running = ""; _message = $"{running} finished"; }
                    running = "";
                    saved = await KeptAsync();
                    verdicts = new List<Verdict>();
                    foreach (var e in schedule.Entries) verdicts.Add(await JudgeAsync(e, site, saved, "", schedule.MinRunMinutes.Value));
                }
                else if (img is { Phase.Text: "Error" or "Aborted" })
                {
                    lock (_gate) { _failedAt[running] = DateTime.UtcNow; _running = ""; _message = $"{running}: {img.Message.Text}"; }
                    running = "";
                }
                else if (mine is null || mine.Status is "Waiting" or "Disabled")
                {
                    // no longer possible (setting, dawn, the Moon): stop it; it is kept and carries on later
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
                        if (r.Ok.Value) { _running = req.Label.Text; _message = $"{req.Label.Text}: {best.Reason}"; }
                        else { _failedAt[req.Label.Text] = DateTime.UtcNow; _message = $"{req.Label.Text} could not start: {r.Error.Text}"; }
                    }
                }
                else lock (_gate) _message = verdicts.Count == 0 ? "the schedule is empty" : verdicts.All(v => v.Status is "Complete" or "Disabled") ? "everything is done" : "nothing can be taken now";
            }
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
            if (_failedAt.TryGetValue(r.Label.Text, out var failed) && DateTime.UtcNow - failed < RetryFailedAfter && r.Label.Text != running)
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
