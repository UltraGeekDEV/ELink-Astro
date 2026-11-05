using System.Text.Json;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.Connections.Models.BaseBinaryConvertibles;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Measures a mount's backlash with plate solves: on each axis the scope arrives at the same spot from below and from
/// above (twice), and where those land apart is the slack in the gears. The latest measurement of each mount is kept (in a
/// file), so the imaging can arrive the same way on every axis that has slack, and need not bother on those that have none.</summary>
public sealed class BacklashService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string? _file;
    private readonly CommandSet _commands;
    private readonly StatePublisher<BacklashState> _publisher;
    private readonly object _gate = new();
    private readonly Dictionary<string, BacklashMeasurement> _mounts = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private string _phase = "Idle", _message = "";

    /// <summary>How far each way from the spot (degrees): well beyond any slack, small enough to be quick.</summary>
    public double StepDegrees { get; set; } = 0.25;
    /// <summary>Arrivals from each side, per axis.</summary>
    public int Repeats { get; set; } = 2;
    /// <summary>Waited after a slew before the shot (the mount and the tube settle; tracking takes up RA slack).</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(3);
    public double ExposureSeconds { get; set; } = 5;

    public BacklashService(TypeSafeEVentNode node, string? stateDir = null)
    {
        _node = node;
        _file = stateDir is null ? null : Path.Combine(stateDir, "backlash.json");
        _commands = new CommandSet(node);
        _publisher = new(node, BacklashIds.State, BacklashIds.GetState, BuildState);
        Load();
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<BinaryConvertibleString, BacklashMeasurement>(BacklashIds.Measure, s => MeasureAsync(s.Text),
            "measure a scope's mount backlash on both axes with a few plate-solved moves each way");
        await _publisher.StartAsync();
    }

    private BacklashState BuildState()
    {
        lock (_gate)
        {
            var s = new BacklashState { Phase = _phase, Message = _message };
            foreach (var m in _mounts.Values) s.Mounts.Add(m);
            return s;
        }
    }

    private async Task Say(string phase, string message)
    {
        lock (_gate) { _phase = phase; _message = message; }
        await _publisher.PublishAsync();
    }

    public async Task<BacklashMeasurement> MeasureAsync(string scopeId)
    {
        var result = new BacklashMeasurement { ScopeId = scopeId, MeasuredUtc = DateTime.UtcNow.ToString("o") };
        if (!await _one.WaitAsync(0)) { result.Message = "a measurement is already running"; return result; }
        RemoteState<PointerState>? pointer = null;
        try
        {
            var snap = (await _node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void, TimeSpan.FromSeconds(10)))?.FirstOrDefault();
            var def = snap?.Scopes.FirstOrDefault(s => s.Id.Text == scopeId);
            if (def is null) { result.Message = $"no scope {scopeId}"; return result; }
            result.MountId = def.Pointers.Count > 0 ? def.Pointers[0].Text : scopeId;
            double scale = await ScaleAsync(def, snap!);

            pointer = new RemoteState<PointerState>(_node, PointerIds.State(scopeId), PointerIds.GetState(scopeId));
            await pointer.StartAsync();
            var here = (await _node.CallFunctionAsync<NOTESVoid, PointerState>(PointerIds.GetState(scopeId), NOTESVoid.Void, TimeSpan.FromSeconds(10)))?.FirstOrDefault();
            if (here is null || here.Phase.Text is "Disconnected" or "Parked" or "Error") { result.Message = $"the mount is {here?.Phase.Text ?? "not answering"}"; return result; }
            double ra0 = here.RaHours.Value, dec0 = here.DecDegrees.Value;
            // near the pole RA moves are tiny on the sky and solves cannot see them: measure a little away from it
            if (Math.Abs(dec0) > 80) dec0 = Math.Sign(dec0) * 80;
            result.DecDegrees = dec0;
            double cos = Math.Cos(dec0 * Math.PI / 180), step = StepDegrees;

            async Task<(double Ra, double Dec)> ArriveAsync(double ra, double dec, string what)
            {
                await Say("Measuring", $"{scopeId}: {what}");
                await GotoAsync(scopeId, pointer!, ra, dec);
                await Task.Delay(Settle);
                var r = (await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
                {
                    ShooterId = scopeId, ExposureSeconds = ExposureSeconds, TimeoutSeconds = 60, HintRaHours = ra, HintDecDegrees = dec, HintRadiusDegrees = 2,
                    ScaleLowArcsecPerPixel = scale > 0 ? scale * 0.85 : 0, ScaleHighArcsecPerPixel = scale > 0 ? scale * 1.18 : 0,
                }, TimeSpan.FromSeconds(150)))?.FirstOrDefault();
                if (r is not { Solved.Value: true }) throw new InvalidOperationException("a plate solve failed: " + (r?.Message.Text ?? "no solver"));
                return (r.RaHours.Value, r.DecDegrees.Value);
            }

            // Dec: arrive at dec0 from below, then from above
            var fromBelow = new List<double>(); var fromAbove = new List<double>();
            for (int i = 0; i < Repeats; i++)
            {
                await GotoAsync(scopeId, pointer, ra0, dec0 - step);
                fromBelow.Add((await ArriveAsync(ra0, dec0, $"Dec from below ({i + 1} of {Repeats})")).Dec);
                await GotoAsync(scopeId, pointer, ra0, dec0 + step);
                fromAbove.Add((await ArriveAsync(ra0, dec0, $"Dec from above ({i + 1} of {Repeats})")).Dec);
            }
            (result.DecArcsec, result.DecSpreadArcsec) = Difference(fromAbove, fromBelow, 3600);

            // RA: the same along RA (in the axis' own angle: a step of RA hours)
            double raStep = step / 15 / Math.Max(cos, 0.1);
            var westOf = new List<double>(); var eastOf = new List<double>();
            for (int i = 0; i < Repeats; i++)
            {
                await GotoAsync(scopeId, pointer, Wrap(ra0 - raStep), dec0);
                westOf.Add((await ArriveAsync(ra0, dec0, $"RA from the west ({i + 1} of {Repeats})")).Ra);
                await GotoAsync(scopeId, pointer, Wrap(ra0 + raStep), dec0);
                eastOf.Add((await ArriveAsync(ra0, dec0, $"RA from the east ({i + 1} of {Repeats})")).Ra);
            }
            // RA differences near 0h/24h
            var east = eastOf.Select(x => Unwrap(x, ra0)).ToList(); var west = westOf.Select(x => Unwrap(x, ra0)).ToList();
            (result.RaArcsec, result.RaSpreadArcsec) = Difference(east, west, 15 * 3600);

            await GotoAsync(scopeId, pointer, here.RaHours.Value, here.DecDegrees.Value);    // back where it was
            result.Ok = true;
            result.Message = FormattableString.Invariant($"RA {result.RaArcsec.Value:0}\" (±{result.RaSpreadArcsec.Value:0}\"), Dec {result.DecArcsec.Value:0}\" (±{result.DecSpreadArcsec.Value:0}\")");
            lock (_gate) _mounts[result.MountId.Text] = result;
            Save();
            SessionLog.Info(_node, scopeId, $"backlash of {result.MountId.Text}: {result.Message.Text}");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            SessionLog.Warn(_node, scopeId, "backlash not measured: " + ex.Message);
            return result;
        }
        finally
        {
            pointer?.Dispose();
            await Say("Idle", result.Ok.Value ? $"{scopeId}: {result.Message.Text}" : $"{scopeId}: not measured: {result.Message.Text}");
            _one.Release();
        }
    }

    /// <summary>Where it lands arriving moving down minus where moving up (the slack), and how much the repeats disagree.</summary>
    private static (double Mean, double Spread) Difference(List<double> moving_down, List<double> moving_up, double toArcsec)
    {
        var d = moving_down.Zip(moving_up, (a, b) => (a - b) * toArcsec).ToList();
        double mean = d.Average(), spread = d.Count > 1 ? d.Max() - d.Min() : double.NaN;
        // the solves' own error: a few arcseconds; a sign of an overshooting mount is kept as it is
        return (mean, spread);
    }

    private static double Wrap(double h) => ((h % 24) + 24) % 24;
    private static double Unwrap(double h, double near) { double d = h - near; if (d > 12) h -= 24; else if (d < -12) h += 24; return h; }

    private async Task GotoAsync(string scopeId, RemoteState<PointerState> pointer, double ra, double dec)
    {
        var go = await Commands.CallAsync(_node, PointerIds.Goto(scopeId), new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" }, TimeSpan.FromSeconds(30));
        if (!go.Ok.Value) throw new InvalidOperationException("the mount refused a move: " + go.Error.Text);
        await Task.Delay(300);
        var p = await pointer.WaitAsync(s => s.OnTarget.Value || s.Phase.Text is "Error" or "Parked" or "Disconnected", TimeSpan.FromMinutes(3));
        if (!p.OnTarget.Value) throw new InvalidOperationException($"the mount is {p.Phase.Text}");
    }

    /// <summary>The scope's imaging camera's pixel scale, for a narrow (quick) solve; 0 = not known.</summary>
    private async Task<double> ScaleAsync(ScopeDefinition def, CompositionSnapshot snap)
    {
        foreach (var sh in def.Shooters)
        {
            if (!snap.Trains.Any(t => t.Id.Text == sh.Id.Text)) continue;
            try
            {
                var t = (await _node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(sh.Id.Text), NOTESVoid.Void, TimeSpan.FromSeconds(5)))?.FirstOrDefault();
                var c = t?.Cameras.FirstOrDefault(c => c.Role.Text == "Imaging" && c.PixelScaleArcsec.Value > 0);
                if (c is not null) return c.PixelScaleArcsec.Value;
            }
            catch (Exception) { }
        }
        return 0;
    }

    private record Stored(string MountId, string ScopeId, double Ra, double Dec, double RaSpread, double DecSpread, double AtDec, string Utc, string Message);

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            foreach (var s in JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_file)) ?? new())
                _mounts[s.MountId] = new BacklashMeasurement
                {
                    MountId = s.MountId, ScopeId = s.ScopeId, RaArcsec = s.Ra, DecArcsec = s.Dec, RaSpreadArcsec = s.RaSpread, DecSpreadArcsec = s.DecSpread,
                    DecDegrees = s.AtDec, MeasuredUtc = s.Utc, Ok = true, Message = s.Message,
                };
        }
        catch (Exception) { }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            List<Stored> list;
            lock (_gate) list = _mounts.Values.Select(m => new Stored(m.MountId.Text, m.ScopeId.Text, m.RaArcsec.Value, m.DecArcsec.Value,
                Nan0(m.RaSpreadArcsec.Value), Nan0(m.DecSpreadArcsec.Value), m.DecDegrees.Value, m.MeasuredUtc.Text, m.Message.Text)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".new", JsonSerializer.Serialize(list));
            File.Move(_file + ".new", _file, true);
        }
        catch (Exception) { }
    }
    private static double Nan0(double v) => double.IsFinite(v) ? v : 0;

    public ValueTask DisposeAsync() { _commands.Dispose(); _publisher.Dispose(); return ValueTask.CompletedTask; }
}
