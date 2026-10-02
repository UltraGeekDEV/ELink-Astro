using System.Text.Json;
using ELink.Contracts.Composition;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using ELink.Contracts.Equipment;
using ELink.Core;

namespace ELink.Compose;

/// <summary>Builds and keeps the glue objects (mount pointers, camera shooters, smart scopes) a user composed, offers
/// them for definition over EVent, and remembers them in a JSON file. Knows equipment only by EVent ID.</summary>
public sealed class CompositionHost : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string? _file;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (MountPointerDefinition Def, MountPointer Item)> _pointers = new();
    private readonly Dictionary<string, (CameraShooterDefinition Def, CameraShooter Item)> _shooters = new();
    private readonly Dictionary<string, (ScopeDefinition Def, SmartScope Item)> _scopes = new();
    private readonly Dictionary<string, (GuiderDefinition Def, Guider Item)> _guiders = new();
    private readonly CommandSet _commands;

    /// <param name="persistFile">where definitions are saved; null keeps them in memory only</param>
    public CompositionHost(TypeSafeEVentNode node, string? persistFile = null)
    {
        _node = node; _file = persistFile;
        _commands = new CommandSet(node);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<MountPointerDefinition, CommandResult>(ScopeIds.DefineMountPointer, d => Guard(() => DefineMountPointerAsync(d)), "make a Mount usable as a Pointer");
        await _commands.AddAsync<CameraShooterDefinition, CommandResult>(ScopeIds.DefineCameraShooter, d => Guard(() => DefineCameraShooterAsync(d)), "make a Camera (and filter wheel) usable as a Shooter");
        await _commands.AddAsync<GuiderDefinition, CommandResult>(ScopeIds.DefineGuider, d => Guard(() => DefineGuiderAsync(d)), "make a guide camera and a guide output into a Guider");
        await _commands.AddAsync<ScopeDefinition, CommandResult>(ScopeIds.Define, d => Guard(() => DefineScopeAsync(d)), "create or replace a smart scope");
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(ScopeIds.Remove, k => Guard(() => RemoveAsync(k.Text)), "remove 'Scope:<id>', 'Pointer:<id>' or 'Shooter:<id>'");
        await _commands.AddAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, _ => Task.FromResult(Snapshot()), "everything this host composes");
        await LoadAsync();
    }

    public CompositionSnapshot Snapshot()
    {
        var s = new CompositionSnapshot();
        foreach (var (d, _) in _pointers.Values) s.MountPointers.Add(d);
        foreach (var (d, _) in _shooters.Values) s.CameraShooters.Add(d);
        foreach (var (d, _) in _scopes.Values) s.Scopes.Add(d);
        foreach (var (d, _) in _guiders.Values) s.Guiders.Add(d);
        return s;
    }

    private async Task<CommandResult> Guard(Func<Task<CommandResult>> action)
    {
        await _lock.WaitAsync();
        try
        {
            var r = await action();
            if (r.Ok.Value) { Save(); await Notify(); }
            return r;
        }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
        finally { _lock.Release(); }
    }

    private static bool BadId(string id) => id == "" || id.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-'));

    public async Task<CommandResult> DefineMountPointerAsync(MountPointerDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id) || BadId(d.MountId.Text)) return CommandResult.Fail("ids may only contain letters, digits, '_' and '-'");
        if (_pointers.Remove(id, out var old)) await old.Item.DisposeAsync();
        var item = new MountPointer(_node, id, d.MountId.Text);
        await item.StartAsync();
        _pointers[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    public async Task<CommandResult> DefineCameraShooterAsync(CameraShooterDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id) || BadId(d.CameraId.Text) || (d.FilterWheelId.Text != "" && BadId(d.FilterWheelId.Text)))
            return CommandResult.Fail("ids may only contain letters, digits, '_' and '-'");
        if (_shooters.Remove(id, out var old)) await old.Item.DisposeAsync();
        var item = new CameraShooter(_node, id, d.CameraId.Text, d.FilterWheelId.Text == "" ? null : d.FilterWheelId.Text);
        await item.StartAsync();
        _shooters[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    private static readonly string[] Outputs = ["Pulse", "Correction"];
    private static readonly string[] DecModes = ["Auto", "North", "South", "Off"];

    public async Task<CommandResult> DefineGuiderAsync(GuiderDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id) || BadId(d.ShooterId.Text)) return CommandResult.Fail("ids may only contain letters, digits, '_' and '-'");
        if (!Outputs.Contains(d.Output.Text)) return CommandResult.Fail("Output is Pulse or Correction");
        if (d.Output.Text == "Pulse" && BadId(d.GuidePortId.Text)) return CommandResult.Fail("Pulse output needs a GuidePortId");
        if (d.Output.Text == "Correction" && BadId(d.TargetId.Text)) return CommandResult.Fail("Correction output needs a TargetId");
        if (d.MountId.Text != "" && BadId(d.MountId.Text)) return CommandResult.Fail("bad MountId");
        if (!DecModes.Contains(d.DecMode.Text)) return CommandResult.Fail("DecMode is Auto, North, South or Off");
        if (!(d.ExposureSeconds.Value > 0)) return CommandResult.Fail("the guide exposure must be positive");
        if (_guiders.Remove(id, out var old)) await old.Item.DisposeAsync();
        var item = new Guider(_node, d);
        await item.StartAsync();
        _guiders[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    public async Task<CommandResult> DefineScopeAsync(ScopeDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id)) return CommandResult.Fail("ids may only contain letters, digits, '_' and '-'");
        if (d.Pointers.Any(p => p.Text == id) || d.Shooters.Any(s => s.Id.Text == id)) return CommandResult.Fail("a scope cannot contain itself");
        if (_scopes.Remove(id, out var old)) await old.Item.DisposeAsync();
        var item = new SmartScope(_node, d);
        await item.StartAsync();
        _scopes[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    public async Task<CommandResult> RemoveAsync(string key)
    {
        int colon = key.IndexOf(':');
        if (colon <= 0) return CommandResult.Fail("expected 'Scope:<id>', 'Pointer:<id>', 'Shooter:<id>' or 'Guider:<id>'");
        string kind = key[..colon], id = key[(colon + 1)..];
        switch (kind)
        {
            case "Scope" when _scopes.Remove(id, out var s): await s.Item.DisposeAsync(); return CommandResult.Success();
            case "Pointer" when _pointers.Remove(id, out var p): await p.Item.DisposeAsync(); return CommandResult.Success();
            case "Shooter" when _shooters.Remove(id, out var h): await h.Item.DisposeAsync(); return CommandResult.Success();
            case "Guider" when _guiders.Remove(id, out var g): await g.Item.DisposeAsync(); return CommandResult.Success();
            default: return CommandResult.Fail($"no such {kind} '{id}'");
        }
    }

    private async Task Notify()
    {
        try { await _node.FireEventAsync(ScopeIds.Changed, Snapshot()); } catch (ObjectDisposedException) { }
    }

    // ---- persistence ------------------------------------------------------------------------------------------

    private sealed record Saved(List<(string Id, string Mount)> MountPointers, List<(string Id, string Camera, string Wheel)> CameraShooters,
        List<ScopeSaved> Scopes, List<GuiderSaved>? Guiders = null);
    private sealed record ScopeSaved(string Id, string Name, List<string> Pointers, List<ShooterSaved> Shooters,
        string? GuiderId = null, int DitherEvery = 0, double DitherPixels = 5, double SettlePixels = 1.5, double SettleSeconds = 10, double SettleTimeoutSeconds = 120);
    private sealed record GuiderSaved(string Id, string Shooter, string Output, string GuidePort, string Target, string Mount, double Exposure, double PixelScale,
        double? CameraAngle, bool Solve, double RaAggressiveness, double DecAggressiveness, double MinMove, int MaxPulse, int CalibrationStep, double CalibrationPixels, string DecMode);
    private sealed record ShooterSaved(string Id, double East, double North);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    private void Save()
    {
        if (_file is null) return;
        var saved = new Saved(
            _pointers.Values.Select(x => (x.Def.Id.Text, x.Def.MountId.Text)).ToList(),
            _shooters.Values.Select(x => (x.Def.Id.Text, x.Def.CameraId.Text, x.Def.FilterWheelId.Text)).ToList(),
            _scopes.Values.Select(x => new ScopeSaved(x.Def.Id.Text, x.Def.DisplayName.Text, x.Def.Pointers.Select(p => p.Text).ToList(),
                x.Def.Shooters.Select(s => new ShooterSaved(s.Id.Text, s.OffsetEastArcmin.Value, s.OffsetNorthArcmin.Value)).ToList(),
                x.Def.GuiderId.Text, x.Def.DitherEvery.Value, x.Def.DitherPixels.Value, x.Def.SettlePixels.Value, x.Def.SettleSeconds.Value, x.Def.SettleTimeoutSeconds.Value)).ToList(),
            _guiders.Values.Select(x => x.Def).Select(g => new GuiderSaved(g.Id.Text, g.ShooterId.Text, g.Output.Text, g.GuidePortId.Text, g.TargetId.Text, g.MountId.Text,
                g.ExposureSeconds.Value, g.PixelScaleArcsec.Value, double.IsNaN(g.CameraAngleDegrees.Value) ? null : g.CameraAngleDegrees.Value, g.SolveOrientation.Value,
                g.RaAggressiveness.Value, g.DecAggressiveness.Value, g.MinMovePixels.Value, g.MaxPulseMs.Value, g.CalibrationStepMs.Value, g.CalibrationPixels.Value, g.DecMode.Text)).ToList());
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Json));
        File.Move(tmp, _file, true);
    }

    private async Task LoadAsync()
    {
        if (_file is null || !File.Exists(_file)) return;
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file), Json); }
        catch (Exception ex) { Console.Error.WriteLine($"[compose] cannot read {_file}: {ex.Message}"); return; }
        if (saved is null) return;
        await _lock.WaitAsync();
        try
        {
            foreach (var (id, mount) in saved.MountPointers) await DefineMountPointerAsync(new MountPointerDefinition { Id = id, MountId = mount });
            foreach (var (id, cam, wheel) in saved.CameraShooters) await DefineCameraShooterAsync(new CameraShooterDefinition { Id = id, CameraId = cam, FilterWheelId = wheel });
            foreach (var g in saved.Guiders ?? [])
                await DefineGuiderAsync(new GuiderDefinition
                {
                    Id = g.Id, ShooterId = g.Shooter, Output = g.Output, GuidePortId = g.GuidePort, TargetId = g.Target, MountId = g.Mount, ExposureSeconds = g.Exposure,
                    PixelScaleArcsec = g.PixelScale, CameraAngleDegrees = g.CameraAngle ?? double.NaN, SolveOrientation = g.Solve, RaAggressiveness = g.RaAggressiveness,
                    DecAggressiveness = g.DecAggressiveness, MinMovePixels = g.MinMove, MaxPulseMs = g.MaxPulse, CalibrationStepMs = g.CalibrationStep,
                    CalibrationPixels = g.CalibrationPixels, DecMode = g.DecMode,
                });
            foreach (var sc in saved.Scopes)
            {
                var d = new ScopeDefinition
                {
                    Id = sc.Id, DisplayName = sc.Name, GuiderId = sc.GuiderId ?? "", DitherEvery = sc.DitherEvery, DitherPixels = sc.DitherPixels,
                    SettlePixels = sc.SettlePixels, SettleSeconds = sc.SettleSeconds, SettleTimeoutSeconds = sc.SettleTimeoutSeconds,
                };
                foreach (var p in sc.Pointers) d.Pointers.Add(p);
                foreach (var s in sc.Shooters) d.Shooters.Add(new ScopeShooterRef { Id = s.Id, OffsetEastArcmin = s.East, OffsetNorthArcmin = s.North });
                await DefineScopeAsync(d);
            }
        }
        finally { _lock.Release(); }
    }

    private static MountPointerDefinition Clone(MountPointerDefinition d) => new() { Id = d.Id.Text, MountId = d.MountId.Text };
    private static CameraShooterDefinition Clone(CameraShooterDefinition d) => new() { Id = d.Id.Text, CameraId = d.CameraId.Text, FilterWheelId = d.FilterWheelId.Text };
    private static GuiderDefinition Clone(GuiderDefinition d)
    {
        var c = new GuiderDefinition();
        Span<byte> bytes = d.ToBytes();
        c.FromBytes(ref bytes);
        return c;
    }

    private static ScopeDefinition Clone(ScopeDefinition d)
    {
        var c = new ScopeDefinition
        {
            Id = d.Id.Text, DisplayName = d.DisplayName.Text, GuiderId = d.GuiderId.Text, DitherEvery = d.DitherEvery.Value, DitherPixels = d.DitherPixels.Value,
            SettlePixels = d.SettlePixels.Value, SettleSeconds = d.SettleSeconds.Value, SettleTimeoutSeconds = d.SettleTimeoutSeconds.Value,
        };
        foreach (var p in d.Pointers) c.Pointers.Add(p.Text);
        foreach (var s in d.Shooters) c.Shooters.Add(new ScopeShooterRef { Id = s.Id.Text, OffsetEastArcmin = s.OffsetEastArcmin.Value, OffsetNorthArcmin = s.OffsetNorthArcmin.Value });
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Dispose();
        foreach (var s in _scopes.Values) await s.Item.DisposeAsync();
        foreach (var g in _guiders.Values) await g.Item.DisposeAsync();
        foreach (var s in _shooters.Values) await s.Item.DisposeAsync();
        foreach (var p in _pointers.Values) await p.Item.DisposeAsync();
        _scopes.Clear(); _shooters.Clear(); _pointers.Clear(); _guiders.Clear();
    }
}
