using System.Text.Json;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Automatic centring. It follows a Mount; whenever the mount finishes a slew to a new target — whoever commanded it,
/// ELink, other software or a hand controller — it plate-solves a frame from the guide camera and corrects until the target is
/// within tolerance: by syncing the mount on the solved position and re-slewing, or, when a sync does not help (some mounts and
/// the INDI simulator ignore them for pointing), by aiming off by the measured error.
/// <para>The guide scope and the primary need not be aligned: the offset between their centres is learned once by solving both
/// at the same pointing, and from then on the guide scope is aimed so that the primary lands on the target. The offset is kept
/// per pier side (a meridian flip turns it over) and survives restarts.</para></summary>
public sealed class CenteringService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string? _file;
    private readonly CommandSet _commands;
    private readonly StatePublisher<CenteringState> _publisher;
    private readonly object _gate = new();
    private CenteringState _state = new() { Phase = "Idle" };
    private CenteringConfig _config = new();
    private RemoteState<MountState>? _mount;
    private bool _sawSlew;
    private Task? _run;
    private CancellationTokenSource? _cts;
    private readonly List<(double Ra, double Dec, DateTime At)> _own = new();
    private (double Ra, double Dec)? _lastCentred;
    private Offset? _offset;

    private sealed record Offset(double EastDegrees, double NorthDegrees, string PierSide);

    /// <summary>A slew must end at least this far from the last centred target to count as a new target.</summary>
    public double NewTargetArcmin { get; set; } = 2.0;
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1);

    public CenteringService(TypeSafeEVentNode node, string? persistFile = null)
    {
        _node = node; _file = persistFile;
        _commands = new CommandSet(node);
        _publisher = new(node, CenteringIds.State, CenteringIds.GetState, BuildState);
    }

    public async Task StartAsync()
    {
        Load();
        await _commands.AddAsync<CenteringConfig, CommandResult>(CenteringIds.Configure, ConfigureAsync, "what to watch and which cameras to use for automatic centring");
        await _commands.AddAsync<SkyTarget, CommandResult>(CenteringIds.CenterNow, CenterNowAsync, "centre now on a J2000 position (NaN RA = the mount's current target)");
        await _commands.AddAsync<NOTESVoid, CommandResult>(CenteringIds.CalibrateOffset, _ => CalibrateAsync(), "solve guide and primary at this pointing and keep the offset between them");
        await _commands.AddAsync<NOTESVoid, CommandResult>(CenteringIds.ClearOffset, async _ => { lock (_gate) _offset = null; Save(); await _publisher.PublishAsync(); return CommandResult.Success(); }, "forget the guide-to-primary offset");
        await _publisher.StartAsync();
        await FollowMountAsync();
    }

    // ---- configuration ------------------------------------------------------------------------------------------

    private async Task<CommandResult> ConfigureAsync(CenteringConfig c)
    {
        if (c.MountId.Text == "") return CommandResult.Fail("MountId is required");
        if (c.GuideShooterId.Text == "") return CommandResult.Fail("GuideShooterId is required (the camera whose frames are solved)");
        if (!(c.ExposureSeconds.Value > 0)) return CommandResult.Fail("the exposure must be positive");
        if (!(c.ToleranceArcmin.Value > 0)) return CommandResult.Fail("the tolerance must be positive");
        bool mountChanged;
        lock (_gate) { mountChanged = _config.MountId.Text != c.MountId.Text; _config = Copy(c); }
        Save();
        if (mountChanged || _mount is null) await FollowMountAsync();
        await Set(s => { s.Phase = c.Enabled.Value ? "Watching" : "Idle"; s.Message = ""; });
        return CommandResult.Success();
    }

    private CenteringConfig Config() { lock (_gate) return _config; }

    private async Task FollowMountAsync()
    {
        _mount?.Dispose(); _mount = null;
        var id = Config().MountId.Text;
        if (id == "") return;
        _mount = new RemoteState<MountState>(_node, EquipmentIds.State(DeviceKinds.Mount, id), EquipmentIds.GetState(DeviceKinds.Mount, id));
        _mount.Changed += OnMount;
        await _mount.StartAsync();
        if (Config().Enabled.Value) await Set(s => s.Phase = "Watching");
    }

    // ---- watching the mount -------------------------------------------------------------------------------------

    private static (double Ra, double Dec) J2000(double ra, double dec, string epoch) =>
        epoch == "JNow" ? Precession.DateToJ2000(ra, dec, DateTime.UtcNow) : (ra, dec);

    private bool IsOwn(double ra, double dec)
    {
        lock (_gate)
        {
            _own.RemoveAll(o => DateTime.UtcNow - o.At > TimeSpan.FromMinutes(15));
            return _own.Any(o => Sky.SeparationDegrees(o.Ra, o.Dec, ra, dec) * 60 < 1.0);
        }
    }

    private void Remember(double ra, double dec) { lock (_gate) _own.Add((ra, dec, DateTime.UtcNow)); }

    /// <summary>A slew that ends on a target this service did not command starts a centring.</summary>
    private void OnMount(MountState m)
    {
        if (m.Phase.Text is "Slewing") { _sawSlew = true; return; }
        if (!_sawSlew || m.Phase.Text is not ("Tracking" or "Idle")) return;
        _sawSlew = false;
        var c = Config();
        if (!c.Enabled.Value) return;
        lock (_gate) if (_run is { IsCompleted: false }) return;     // our own corrective slews

        // where it was sent: the mount's own target when it says, else where it ended up
        var (ra, dec) = double.IsNaN(m.TargetRaHours.Value)
            ? J2000(m.RaHours.Value, m.DecDegrees.Value, m.Epoch.Text)
            : J2000(m.TargetRaHours.Value, m.TargetDecDegrees.Value, m.Epoch.Text);
        if (IsOwn(ra, dec)) return;
        if (_lastCentred is { } last && Sky.SeparationDegrees(last.Ra, last.Dec, ra, dec) * 60 < NewTargetArcmin) return;
        Start(ra, dec);
    }

    private void Start(double raHours, double decDegrees)
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _run = Task.Run(() => CenterAsync(raHours, decDegrees, ct));
        }
    }

    private async Task<CommandResult> CenterNowAsync(SkyTarget t)
    {
        if (_mount?.Latest is not { Connected.Value: true } m) return CommandResult.Fail("the mount is not connected");
        double ra = t.RaHours.Value, dec = t.DecDegrees.Value;
        if (double.IsNaN(ra))
            (ra, dec) = double.IsNaN(m.TargetRaHours.Value) ? J2000(m.RaHours.Value, m.DecDegrees.Value, m.Epoch.Text) : J2000(m.TargetRaHours.Value, m.TargetDecDegrees.Value, m.Epoch.Text);
        else (ra, dec) = J2000(ra, dec, t.Epoch.Text);
        lock (_gate) if (_run is { IsCompleted: false }) return CommandResult.Fail("already centring");
        Start(ra, dec);
        await Task.Yield();
        return CommandResult.Success();
    }

    // ---- centring -----------------------------------------------------------------------------------------------

    /// <summary>Where the guide scope must point for the primary to be on the target.</summary>
    private (double Ra, double Dec) GuideAimFor(double ra, double dec, out string note)
    {
        note = "";
        var c = Config(); var off = _offset;
        if (!c.UseOffset.Value || off is null) return (ra, dec);
        double e = off.EastDegrees, n = off.NorthDegrees;
        string pier = _mount?.Latest?.PierSide.Text ?? "Unknown";
        if (off.PierSide is "East" or "West" && pier is "East" or "West" && pier != off.PierSide) { e = -e; n = -n; note = " (offset turned over: other pier side)"; }
        return Gnomonic.ToSky(ra, dec, -e, -n);
    }

    private async Task CenterAsync(double targetRa, double targetDec, CancellationToken ct)
    {
        var c = Config();
        string mount = c.MountId.Text, guide = c.GuideShooterId.Text;
        var (gRa, gDec) = GuideAimFor(targetRa, targetDec, out string note);
        await Set(s => { s.Phase = "Solving"; s.Iteration = 0; s.ErrorArcmin = double.NaN; s.TargetRaHours = targetRa; s.TargetDecDegrees = targetDec; s.Message = "new target" + note; });
        double tol = c.ToleranceArcmin.Value;
        bool syncWorks = c.SyncMount.Value;
        double? previous = null; bool lastWasSync = false;
        (double Ra, double Dec) aim = (gRa, gDec);              // what the mount is told when aiming off
        try
        {
            for (int i = 1; i <= Math.Clamp(c.MaxIterations.Value, 1, 20); i++)
            {
                ct.ThrowIfCancellationRequested();
                int iteration = i;
                await Set(s => { s.Phase = "Solving"; s.Iteration = iteration; });
                var hint = _mount?.Latest is { } m ? J2000(m.RaHours.Value, m.DecDegrees.Value, m.Epoch.Text) : (gRa, gDec);
                var solved = await SolveAsync(guide, c.ExposureSeconds.Value, hint.Item1, hint.Item2);
                if (!solved.Solved.Value) throw new InvalidOperationException("the guide frame did not solve: " + solved.Message.Text);
                double err = Sky.SeparationDegrees(solved.RaHours.Value, solved.DecDegrees.Value, gRa, gDec) * 60;
                await Set(s => s.ErrorArcmin = err);
                if (err <= tol)
                {
                    lock (_gate) _lastCentred = (targetRa, targetDec);
                    await Set(s => { s.Phase = "Centered"; s.Message = $"within {err:0.0}' of the target after {iteration - 1} correction(s){note}"; s.Centerings = s.Centerings.Value + 1; });
                    return;
                }
                if (lastWasSync && previous is { } p && err > 0.6 * p)
                {
                    syncWorks = false;                           // the sync did not move the pointing: aim off instead
                    await Set(s => s.Message = "the mount does not take syncs for pointing here; aiming off by the error instead");
                }
                previous = err;
                ct.ThrowIfCancellationRequested();
                await Set(s => s.Phase = "Correcting");
                if (syncWorks)
                {
                    var sync = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, mount, "Sync"), new SkyTarget { RaHours = solved.RaHours.Value, DecDegrees = solved.DecDegrees.Value, Epoch = "J2000" });
                    if (!sync.Ok.Value) { syncWorks = false; await Set(s => s.Message = "sync refused (" + sync.Error.Text + "); aiming off instead"); }
                    else { await SlewAsync(mount, gRa, gDec, ct); lastWasSync = true; continue; }
                }
                // aim off: move the aim point by the measured error, in the tangent plane at the target
                var (ee, en) = Gnomonic.FromSky(gRa, gDec, solved.RaHours.Value, solved.DecDegrees.Value);
                var (ae, an) = Gnomonic.FromSky(gRa, gDec, aim.Ra, aim.Dec);
                aim = Gnomonic.ToSky(gRa, gDec, ae - ee, an - en);
                await SlewAsync(mount, aim.Ra, aim.Dec, ct);
                lastWasSync = false;
            }
            await Set(s => { s.Phase = "Failed"; s.Message = $"not within {tol}' after {c.MaxIterations.Value} tries (last error {s.ErrorArcmin.Value:0.0}')"; });
        }
        catch (OperationCanceledException) { await Set(s => { s.Phase = "Idle"; s.Message = "cancelled"; }); }
        catch (Exception ex) { await Set(s => { s.Phase = "Failed"; s.Message = ex.Message; }); }
        finally { _sawSlew = false; }
    }

    private async Task SlewAsync(string mount, double ra, double dec, CancellationToken ct)
    {
        Remember(ra, dec);
        var go = await Commands.CallAsync(_node, EquipmentIds.Command(DeviceKinds.Mount, mount, "Goto"), new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" });
        if (!go.Ok.Value) throw new InvalidOperationException("the mount refused the slew: " + go.Error.Text);
        var m = _mount ?? throw new InvalidOperationException("no mount");
        // the slew may be over before it is ever reported as one; either see it start, or see the mount arrive
        try
        {
            await m.WaitAsync(s => s.Phase.Text == "Slewing" ||
                Sky.SeparationDegrees(J2000(s.RaHours.Value, s.DecDegrees.Value, s.Epoch.Text).Ra, J2000(s.RaHours.Value, s.DecDegrees.Value, s.Epoch.Text).Dec, ra, dec) * 60 < 0.5, TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException) { }
        await m.WaitAsync(s => s.Phase.Text is not ("Slewing" or "Parking"), TimeSpan.FromMinutes(5), ct);
        await Task.Delay(SettleDelay, ct);
    }

    private async Task<SolveResult> SolveAsync(string shooter, double seconds, double hintRa, double hintDec)
    {
        var answers = await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
        {
            ShooterId = shooter, ExposureSeconds = seconds, HintRaHours = hintRa, HintDecDegrees = hintDec, HintRadiusDegrees = 8, TimeoutSeconds = 90,
        }, TimeSpan.FromSeconds(seconds + 180));
        if (answers is not { Count: > 0 }) return new SolveResult { Message = "no plate solver on the mesh" };
        return answers[0];
    }

    // ---- offset between guide scope and primary -------------------------------------------------------------------

    private async Task<CommandResult> CalibrateAsync()
    {
        var c = Config();
        if (c.PrimaryShooterId.Text == "" || c.GuideShooterId.Text == "") return CommandResult.Fail("both GuideShooterId and PrimaryShooterId must be configured");
        lock (_gate) if (_run is { IsCompleted: false }) return CommandResult.Fail("busy centring");
        await Set(s => { s.Phase = "Calibrating"; s.Message = "solving guide and primary at the same pointing"; });
        var hint = _mount?.Latest is { } m ? J2000(m.RaHours.Value, m.DecDegrees.Value, m.Epoch.Text) : (double.NaN, double.NaN);
        var g = await SolveAsync(c.GuideShooterId.Text, c.ExposureSeconds.Value, hint.Item1, hint.Item2);
        var p = await SolveAsync(c.PrimaryShooterId.Text, c.ExposureSeconds.Value, hint.Item1, hint.Item2);
        if (!g.Solved.Value || !p.Solved.Value)
        {
            string why = !g.Solved.Value ? "guide: " + g.Message.Text : "primary: " + p.Message.Text;
            await Set(s => { s.Phase = c.Enabled.Value ? "Watching" : "Idle"; s.Message = "calibration failed, " + why; });
            return CommandResult.Fail("calibration failed, " + why);
        }
        var (e, n) = Gnomonic.FromSky(g.RaHours.Value, g.DecDegrees.Value, p.RaHours.Value, p.DecDegrees.Value);
        string pier = _mount?.Latest?.PierSide.Text ?? "Unknown";
        lock (_gate) _offset = new Offset(e, n, pier);
        Save();
        await Set(s => { s.Phase = c.Enabled.Value ? "Watching" : "Idle"; s.Message = $"offset learned: the primary looks {e * 60:0.0}' east, {n * 60:0.0}' north of the guide scope"; });
        return CommandResult.Success();
    }

    // ---- state and persistence -------------------------------------------------------------------------------------

    private async Task Set(Action<CenteringState> change)
    {
        lock (_gate) change(_state);
        await _publisher.PublishAsync();
    }

    private CenteringState BuildState()
    {
        lock (_gate)
        {
            var s = new CenteringState
            {
                Phase = _state.Phase.Text, Message = _state.Message.Text, Config = Copy(_config), TargetRaHours = _state.TargetRaHours.Value,
                TargetDecDegrees = _state.TargetDecDegrees.Value, Iteration = _state.Iteration.Value, ErrorArcmin = _state.ErrorArcmin.Value,
                Centerings = _state.Centerings.Value, OffsetKnown = _offset is not null,
            };
            if (_offset is { } o) { s.OffsetEastArcmin = o.EastDegrees * 60; s.OffsetNorthArcmin = o.NorthDegrees * 60; s.OffsetPierSide = o.PierSide; }
            return s;
        }
    }

    private static CenteringConfig Copy(CenteringConfig c) => new()
    {
        Enabled = c.Enabled.Value, MountId = c.MountId.Text, GuideShooterId = c.GuideShooterId.Text, PrimaryShooterId = c.PrimaryShooterId.Text,
        ExposureSeconds = c.ExposureSeconds.Value, ToleranceArcmin = c.ToleranceArcmin.Value, MaxIterations = c.MaxIterations.Value,
        UseOffset = c.UseOffset.Value, SyncMount = c.SyncMount.Value,
    };

    private sealed record Saved(bool Enabled, string Mount, string Guide, string Primary, double Exposure, double Tolerance, int MaxIterations,
        bool UseOffset, bool SyncMount, double? OffsetEast, double? OffsetNorth, string? OffsetPier);

    private void Save()
    {
        if (_file is null) return;
        Saved saved;
        lock (_gate)
        {
            var c = _config;
            saved = new Saved(c.Enabled.Value, c.MountId.Text, c.GuideShooterId.Text, c.PrimaryShooterId.Text, c.ExposureSeconds.Value, c.ToleranceArcmin.Value,
                c.MaxIterations.Value, c.UseOffset.Value, c.SyncMount.Value, _offset?.EastDegrees, _offset?.NorthDegrees, _offset?.PierSide);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_file + ".tmp", _file, true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[centering] cannot save {_file}: {ex.Message}"); }
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file));
            if (s is null) return;
            lock (_gate)
            {
                _config = new CenteringConfig
                {
                    Enabled = s.Enabled, MountId = s.Mount, GuideShooterId = s.Guide, PrimaryShooterId = s.Primary, ExposureSeconds = s.Exposure,
                    ToleranceArcmin = s.Tolerance, MaxIterations = s.MaxIterations, UseOffset = s.UseOffset, SyncMount = s.SyncMount,
                };
                if (s.OffsetEast is double e && s.OffsetNorth is double n) _offset = new Offset(e, n, s.OffsetPier ?? "Unknown");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[centering] cannot read {_file}: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(3000));
        _mount?.Dispose(); _commands.Dispose(); _publisher.Dispose();
    }
}
