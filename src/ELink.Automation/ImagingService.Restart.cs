using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>An image survives the station: the one being taken is written down when it starts and crossed out when it ends
/// (done, stopped by a person, or failed). Finding it still there when the station starts means the station stopped under
/// it (a crash, a power cut, a restart), and the image carries on as soon as its scopes can take shots again.</summary>
public sealed partial class ImagingService
{
    /// <summary>Carry on with the image that was being taken when the station stopped; false = wait for a person.</summary>
    public bool ResumeAfterRestart { get; set; } = true;

    /// <summary>How long to keep trying to carry on (devices coming up after a power cut take a while).</summary>
    public TimeSpan ResumeTryFor { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>The wait between tries.</summary>
    public TimeSpan ResumeRetry { get; set; } = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _life = new();
    private Task? _resume;
    private volatile bool _stoppedByPerson;

    private string? RunningFile => _dataDir is null ? null : Path.Combine(_dataDir, "images", "running.bin");

    private void MarkRunning(ImagingRequest r)
    {
        if (RunningFile is not { } file) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file + ".new", r.ToBytes());
            File.Move(file + ".new", file, true);
        }
        catch (Exception ex) { SessionLog.Warn(_node, "imaging", "could not note the image as running (it will not carry on by itself after a restart): " + ex.Message); }
    }

    private void ClearRunning()
    {
        try { if (RunningFile is { } file) File.Delete(file); } catch { }
    }

    /// <summary>An image ended: it is crossed out unless it ended only because the station is stopping.</summary>
    private void Ended(string phase)
    {
        if (phase != "Aborted" || _stoppedByPerson || !_life.IsCancellationRequested) ClearRunning();
    }

    private void StartResuming()
    {
        if (!ResumeAfterRestart || RunningFile is not { } file || !File.Exists(file)) return;
        ImagingRequest r;
        try { Span<byte> bytes = File.ReadAllBytes(file); r = new ImagingRequest(); if (!r.FromBytes(ref bytes)) throw new InvalidDataException("unreadable"); }
        catch (Exception ex) { SessionLog.Warn(_node, "imaging", "an image was running when the station stopped, but its note cannot be read: " + ex.Message); ClearRunning(); return; }
        r.Resume = true;
        _resume = Task.Run(() => ResumeAsync(r, _life.Token));
    }

    private async Task ResumeAsync(ImagingRequest r, CancellationToken ct)
    {
        string label = r.Label.Text, last = "";
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }   // the session log starts after this service
        SessionLog.Info(_node, "imaging", $"the station stopped while '{label}' was being taken: carrying on with it");
        var until = DateTime.UtcNow + ResumeTryFor;
        int tries = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate) if (_cts is not null) return;          // somebody started an image meanwhile
                string? waiting = await MountsNotReadyAsync(r, ct);
                if (waiting is null)
                {
                    var res = await StartRunAsync(r);
                    if (res.Ok.Value) { SessionLog.Info(_node, "imaging", $"'{label}' carries on after the restart"); return; }
                    if (res.Error.Text.StartsWith("an image is already")) return;
                    waiting = res.Error.Text;
                }
                if (waiting != last) SessionLog.Warn(_node, "imaging", $"'{label}' cannot carry on yet: {waiting}");
                last = waiting;
                await Set(s => { s.Phase = "Resuming"; s.Label = label; s.Message = $"carrying on with '{label}' after a restart as soon as it can: {waiting}"; });
                if (DateTime.UtcNow > until) break;
                // services still starting are up within seconds; devices after a power cut take longer
                await Task.Delay(++tries <= 6 ? TimeSpan.FromSeconds(Math.Min(5, ResumeRetry.TotalSeconds)) : ResumeRetry, ct);
            }
            if (ct.IsCancellationRequested) return;
            SessionLog.Error(_node, "imaging", $"gave up carrying on with '{label}' after the restart: {last}. It is kept: continue it by hand");
            await Set(s => { s.Phase = "Idle"; s.Message = $"could not carry on with '{label}' after the restart: {last}. It is kept: continue it by hand"; });
            ClearRunning();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Why the image cannot be taken yet (its live stack not up, a mount not connected or parked), or null when it can. A parked mount stays
    /// parked: somebody parked it, or it parks itself when it starts, and unparking a telescope nobody watches is theirs to decide.</summary>
    private async Task<string?> MountsNotReadyAsync(ImagingRequest r, CancellationToken ct)
    {
        if (r.LiveStack.Value)
        {
            // the live stack starts after this service: an image carried on before it is up would have no stack
            LiveStackState? ls = null;
            try { ls = (await _node.CallFunctionAsync<NOTESVoid, LiveStackState>(LiveStackIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(10), ct))?.FirstOrDefault(); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            if (ls is null) return "the live stack is not up yet";
        }
        CompositionSnapshot snap;
        try { snap = await SnapshotAsync(); } catch (Exception ex) { return "no composition yet: " + ex.Message; }
        foreach (var id in r.ScopeIds.Select(x => x.Text))
        {
            var scope = snap.Scopes.FirstOrDefault(s => s.Id.Text == id);
            if (scope is null) return $"no scope {id} yet";
            foreach (var pointer in scope.Pointers.Select(p => p.Text))
            {
                PointerState? p = null;
                try { p = (await _node.CallFunctionAsync<NOTESVoid, PointerState>(PointerIds.GetState(pointer), NOTESVoid.Void, TimeSpan.FromSeconds(10), ct))?.FirstOrDefault(); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                if (p is null) return $"{id}'s mount {pointer} does not answer";
                if (p.Phase.Text == "Disconnected") return $"{id}'s mount is not connected";
                if (p.Phase.Text == "Parked") return $"{id}'s mount is parked (unpark it and it carries on)";
            }
        }
        return null;
    }
}
