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
    private readonly Dictionary<string, (ImagingTrainDefinition Def, ImagingTrain Item)> _trains = new();
    private readonly Dictionary<string, Guider> _scopeGuiders = new();   // guiders scopes own through their inline guide settings
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
        await _commands.AddAsync<ImagingTrainDefinition, CommandResult>(ScopeIds.DefineTrain, d => Guard(() => DefineTrainAsync(d)), "an imaging train: optics, cameras (imaging or guiding), filter wheel, focuser, rotator");
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
        foreach (var (d, _) in _trains.Values) s.Trains.Add(d);
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
    private static string IdError(string what, string id) => id == "" ? $"give the {what} an id" : $"the {what} id '{id}' may only contain letters, digits, '_' and '-' (no spaces)";

    public async Task<CommandResult> DefineMountPointerAsync(MountPointerDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id)) return CommandResult.Fail(IdError("pointer", id));
        if (BadId(d.MountId.Text)) return CommandResult.Fail("pick a mount");
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

    public async Task<CommandResult> DefineTrainAsync(ImagingTrainDefinition d)
    {
        string id = d.Id.Text;
        if (BadId(id)) return CommandResult.Fail(IdError("train", id));
        if (d.Cameras.Count == 0) return CommandResult.Fail("a train needs at least one camera");
        if (d.Cameras.Any(c => BadId(c.CameraId.Text))) return CommandResult.Fail("bad camera id");
        if (d.Cameras.Any(c => c.Role.Text is not ("Imaging" or "Guiding"))) return CommandResult.Fail("a camera's role is Imaging or Guiding");
        if (d.Cameras.GroupBy(c => c.CameraId.Text).Any(g => g.Count() > 1)) return CommandResult.Fail("a camera can only be in a train once");
        foreach (var other in new[] { d.FilterWheelId.Text, d.FocuserId.Text, d.RotatorId.Text })
            if (other != "" && BadId(other)) return CommandResult.Fail($"bad device id {other}");
        if (d.FocalLengthMm.Value < 0 || double.IsNaN(d.FocalLengthMm.Value)) return CommandResult.Fail("focal length must be positive (or 0 if unknown)");
        if (_shooters.ContainsKey(id) || _scopes.ContainsKey(id)) return CommandResult.Fail($"'{id}' is already a shooter or a scope");
        // one camera, one train: two trains would both drive it and both claim its frames
        foreach (var c in d.Cameras)
        {
            if (_trains.Values.FirstOrDefault(t => t.Def.Id.Text != id && t.Def.Cameras.Any(x => x.CameraId.Text == c.CameraId.Text)) is { Def: { } t })
                return CommandResult.Fail($"camera {c.CameraId.Text} is already in train '{t.Id.Text}' (a guide scope needs its own camera, e.g. Guide_Simulator)");
            if (_shooters.Values.FirstOrDefault(x => x.Def.CameraId.Text == c.CameraId.Text) is { Def: { } sh })
                return CommandResult.Fail($"camera {c.CameraId.Text} is already the shooter '{sh.Id.Text}'");
        }
        if (_trains.Remove(id, out var old)) await old.Item.DisposeAsync();
        var item = new ImagingTrain(_node, d);
        await item.StartAsync();
        _trains[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    private static ImagingTrainDefinition Clone(ImagingTrainDefinition d)
    {
        var c = new ImagingTrainDefinition();
        Span<byte> bytes = d.ToBytes();
        c.FromBytes(ref bytes);
        return c;
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
        if (BadId(id)) return CommandResult.Fail(IdError("scope", id));
        if (d.Pointers.Any(p => p.Text == id) || d.Shooters.Any(s => s.Id.Text == id)) return CommandResult.Fail("a scope cannot contain itself");
        // the scope's own guider, from its inline guide settings
        GuiderDefinition? own = null;
        if (d.GuiderId.Text == "" && d.GuideShooterId.Text != "")
        {
            string g = d.GuideShooterId.Text;
            if (BadId(g)) return CommandResult.Fail("bad GuideShooterId");
            if (d.Shooters.Any(sh => sh.Id.Text == g)) return CommandResult.Fail($"'{g}' cannot image and guide at once: guide with another train, or with a train's guide camera ({TrainIds.GuideShooter("<train>")})");
            if (!Outputs.Contains(d.GuideOutput.Text)) return CommandResult.Fail("GuideOutput is Pulse or Correction");
            string mount = d.Pointers.Count > 0 && _pointers.TryGetValue(d.Pointers[0].Text, out var mp) ? mp.Def.MountId.Text : "";
            string port = d.GuidePortId.Text != "" ? d.GuidePortId.Text : mount;
            if (d.GuideOutput.Text == "Pulse" && port == "") return CommandResult.Fail("guiding with pulses needs a mount: tick a pointer (define one from your mount in step 1), or pick a guide port");
            if (d.GuideOutput.Text == "Correction" && BadId(d.GuideTargetId.Text)) return CommandResult.Fail("guiding by corrections needs a GuideTargetId");
            own = new GuiderDefinition
            {
                Id = id + "-guider", ShooterId = g, Output = d.GuideOutput.Text, GuidePortId = d.GuideOutput.Text == "Pulse" ? port : "",
                TargetId = d.GuideOutput.Text == "Correction" ? d.GuideTargetId.Text : "", MountId = mount,
                ExposureSeconds = d.GuideExposureSeconds.Value > 0 ? d.GuideExposureSeconds.Value : 2,
            };
        }
        if (_scopes.Remove(id, out var old)) await old.Item.DisposeAsync();
        if (_scopeGuiders.Remove(id, out var oldGuider)) await oldGuider.DisposeAsync();
        if (own is not null)
        {
            var guider = new Guider(_node, own);
            await guider.StartAsync();
            _scopeGuiders[id] = guider;
        }
        var item = new SmartScope(_node, d, own?.Id.Text);
        await item.StartAsync();
        _scopes[id] = (Clone(d), item);
        return CommandResult.Success();
    }

    public async Task<CommandResult> RemoveAsync(string key)
    {
        int colon = key.IndexOf(':');
        if (colon <= 0) return CommandResult.Fail("expected 'Scope:<id>', 'Pointer:<id>', 'Shooter:<id>', 'Train:<id>' or 'Guider:<id>'");
        string kind = key[..colon], id = key[(colon + 1)..];
        // what a scope is made of cannot be pulled out from under it
        var users = kind switch
        {
            "Train" or "Shooter" => _scopes.Values.Where(x => x.Def.Shooters.Any(r => r.Id.Text == id) || x.Def.GuideShooterId.Text == id || x.Def.GuideShooterId.Text == TrainIds.GuideShooter(id)),
            "Pointer" => _scopes.Values.Where(x => x.Def.Pointers.Any(p => p.Text == id)),
            "Guider" => _scopes.Values.Where(x => x.Def.GuiderId.Text == id),
            _ => Enumerable.Empty<(ScopeDefinition Def, SmartScope Item)>(),
        };
        if (users.FirstOrDefault().Def is { } user) return CommandResult.Fail($"{kind.ToLowerInvariant()} '{id}' is part of scope '{user.Id.Text}': change or remove that scope first");
        switch (kind)
        {
            case "Scope" when _scopes.Remove(id, out var s):
                await s.Item.DisposeAsync();
                if (_scopeGuiders.Remove(id, out var sg)) await sg.DisposeAsync();
                return CommandResult.Success();
            case "Train" when _trains.Remove(id, out var t): await t.Item.DisposeAsync(); return CommandResult.Success();
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
        List<ScopeSaved> Scopes, List<GuiderSaved>? Guiders = null, List<TrainSaved>? Trains = null);
    private sealed record TrainSaved(string Id, string Label, double FocalLength, double Aperture, List<(string Camera, string Role)> Cameras, string Wheel, string Focuser, string Rotator,
        List<(string Camera, double PixelSize, int Width, int Height)>? Sensors = null, List<CameraSettingsSaved>? Settings = null, List<(string Filter, int Steps)>? FocusOffsets = null);
    /// <summary>A train camera's presets and cooling; null = not set (JSON has no NaN).</summary>
    private sealed record CameraSettingsSaved(string Camera, double? Gain, double? Offset, double? CoolTo, double CoolRate, double WarmTo);
    private static double? Opt(double v) => double.IsNaN(v) ? null : v;
    private sealed record ScopeSaved(string Id, string Name, List<string> Pointers, List<ShooterSaved> Shooters,
        string? GuiderId = null, string? GuideShooterId = null, string? GuideOutput = null, string? GuidePortId = null, string? GuideTargetId = null, double GuideExposure = 2, bool CenterAfterSlew = false, string? CenterShooterId = null, double CenterTolerance = 1, double CenterExposure = 3, int CenterTries = 4, bool GradeFrames = true, bool FocusOnStart = false, double RefocusEvery = 0, double RefocusTemp = 0, bool RefocusFilter = false, double RefocusHfr = 0,
        double FocusExposure = 3, int FocusStep = 3000, int FocusSamples = 7, bool MeridianFlip = true, double FlipAfterHours = 0.1, string? SiteId = null, int DitherEvery = 0, double DitherPixels = 5, double SettlePixels = 1.5, double SettleSeconds = 10, double SettleTimeoutSeconds = 120);
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
                x.Def.GuiderId.Text, x.Def.GuideShooterId.Text, x.Def.GuideOutput.Text, x.Def.GuidePortId.Text, x.Def.GuideTargetId.Text, x.Def.GuideExposureSeconds.Value,
                x.Def.CenterAfterSlew.Value, x.Def.CenterShooterId.Text, x.Def.CenterToleranceArcmin.Value, x.Def.CenterExposureSeconds.Value, x.Def.CenterMaxTries.Value,
                x.Def.GradeFrames.Value, x.Def.FocusOnStart.Value, x.Def.RefocusEveryMinutes.Value, x.Def.RefocusTemperatureDelta.Value, x.Def.RefocusOnFilterChange.Value, x.Def.RefocusHfrIncreasePercent.Value,
                x.Def.FocusExposureSeconds.Value, x.Def.FocusStepSize.Value, x.Def.FocusSamples.Value, x.Def.MeridianFlip.Value, x.Def.FlipAfterHours.Value, x.Def.SiteId.Text, x.Def.DitherEvery.Value, x.Def.DitherPixels.Value, x.Def.SettlePixels.Value, x.Def.SettleSeconds.Value, x.Def.SettleTimeoutSeconds.Value)).ToList(),
            _guiders.Values.Select(x => x.Def).Select(g => new GuiderSaved(g.Id.Text, g.ShooterId.Text, g.Output.Text, g.GuidePortId.Text, g.TargetId.Text, g.MountId.Text,
                g.ExposureSeconds.Value, g.PixelScaleArcsec.Value, double.IsNaN(g.CameraAngleDegrees.Value) ? null : g.CameraAngleDegrees.Value, g.SolveOrientation.Value,
                g.RaAggressiveness.Value, g.DecAggressiveness.Value, g.MinMovePixels.Value, g.MaxPulseMs.Value, g.CalibrationStepMs.Value, g.CalibrationPixels.Value, g.DecMode.Text)).ToList(),
            _trains.Values.Select(x => x.Def).Select(t => new TrainSaved(t.Id.Text, t.Label.Text, t.FocalLengthMm.Value, t.ApertureMm.Value,
                t.Cameras.Select(c => (c.CameraId.Text, c.Role.Text)).ToList(), t.FilterWheelId.Text, t.FocuserId.Text, t.RotatorId.Text,
                t.Cameras.Select(c => (c.CameraId.Text, c.PixelSizeUm.Value, c.SensorWidth.Value, c.SensorHeight.Value)).ToList(),
                t.Cameras.Select(c => new CameraSettingsSaved(c.CameraId.Text, Opt(c.Gain.Value), Opt(c.Offset.Value), Opt(c.CoolTo.Value), c.CoolDegreesPerMinute.Value, c.WarmTo.Value)).ToList(),
                t.FocusOffsets.Select(o => (o.Filter.Text, o.Steps.Value)).ToList())).ToList());
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Json));
        File.Move(tmp, _file, true);
    }

    private static void Report(string what, CommandResult r)
    {
        if (!r.Ok.Value) Console.Error.WriteLine($"[compose] {what} not restored: {r.Error.Text}");
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
            foreach (var t in saved.Trains ?? [])
            {
                var d = new ImagingTrainDefinition { Id = t.Id, Label = t.Label, FocalLengthMm = t.FocalLength, ApertureMm = t.Aperture, FilterWheelId = t.Wheel, FocuserId = t.Focuser, RotatorId = t.Rotator };
                foreach (var (cam, role) in t.Cameras)
                {
                    var sensor = t.Sensors?.FirstOrDefault(x => x.Camera == cam);
                    var set = t.Settings?.FirstOrDefault(x => x.Camera == cam);
                    d.Cameras.Add(new TrainCamera
                    {
                        CameraId = cam, Role = role, PixelSizeUm = sensor?.PixelSize ?? 0, SensorWidth = sensor?.Width ?? 0, SensorHeight = sensor?.Height ?? 0,
                        Gain = set?.Gain ?? double.NaN, Offset = set?.Offset ?? double.NaN, CoolTo = set?.CoolTo ?? double.NaN,
                        CoolDegreesPerMinute = set?.CoolRate ?? 3, WarmTo = set?.WarmTo ?? 10,
                    });
                }
                foreach (var (filter, steps) in t.FocusOffsets ?? []) d.FocusOffsets.Add(new FilterFocusOffset { Filter = filter, Steps = steps });
                Report($"train {t.Id}", await DefineTrainAsync(d));
            }
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
                    Id = sc.Id, DisplayName = sc.Name, GuiderId = sc.GuiderId ?? "", GuideShooterId = sc.GuideShooterId ?? "", GuideOutput = sc.GuideOutput ?? "Pulse",
                    GuidePortId = sc.GuidePortId ?? "", GuideTargetId = sc.GuideTargetId ?? "", GuideExposureSeconds = sc.GuideExposure,
                    CenterAfterSlew = sc.CenterAfterSlew, CenterShooterId = sc.CenterShooterId ?? "", CenterToleranceArcmin = sc.CenterTolerance,
                    CenterExposureSeconds = sc.CenterExposure, CenterMaxTries = sc.CenterTries,
                    GradeFrames = sc.GradeFrames, FocusOnStart = sc.FocusOnStart, RefocusEveryMinutes = sc.RefocusEvery, RefocusTemperatureDelta = sc.RefocusTemp, RefocusOnFilterChange = sc.RefocusFilter,
                    RefocusHfrIncreasePercent = sc.RefocusHfr, FocusExposureSeconds = sc.FocusExposure, FocusStepSize = sc.FocusStep, FocusSamples = sc.FocusSamples,
                    MeridianFlip = sc.MeridianFlip, FlipAfterHours = sc.FlipAfterHours, SiteId = sc.SiteId ?? "home", DitherEvery = sc.DitherEvery, DitherPixels = sc.DitherPixels,
                    SettlePixels = sc.SettlePixels, SettleSeconds = sc.SettleSeconds, SettleTimeoutSeconds = sc.SettleTimeoutSeconds,
                };
                foreach (var p in sc.Pointers) d.Pointers.Add(p);
                foreach (var s in sc.Shooters) d.Shooters.Add(new ScopeShooterRef { Id = s.Id, OffsetEastArcmin = s.East, OffsetNorthArcmin = s.North });
                Report($"scope {sc.Id}", await DefineScopeAsync(d));
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
            Id = d.Id.Text, DisplayName = d.DisplayName.Text, GuiderId = d.GuiderId.Text, GuideShooterId = d.GuideShooterId.Text, GuideOutput = d.GuideOutput.Text,
            GuidePortId = d.GuidePortId.Text, GuideTargetId = d.GuideTargetId.Text, GuideExposureSeconds = d.GuideExposureSeconds.Value,
            CenterAfterSlew = d.CenterAfterSlew.Value, CenterShooterId = d.CenterShooterId.Text, CenterToleranceArcmin = d.CenterToleranceArcmin.Value,
            CenterExposureSeconds = d.CenterExposureSeconds.Value, CenterMaxTries = d.CenterMaxTries.Value,
            GradeFrames = d.GradeFrames.Value, FocusOnStart = d.FocusOnStart.Value, RefocusEveryMinutes = d.RefocusEveryMinutes.Value, RefocusTemperatureDelta = d.RefocusTemperatureDelta.Value,
            RefocusOnFilterChange = d.RefocusOnFilterChange.Value, RefocusHfrIncreasePercent = d.RefocusHfrIncreasePercent.Value,
            FocusExposureSeconds = d.FocusExposureSeconds.Value, FocusStepSize = d.FocusStepSize.Value, FocusSamples = d.FocusSamples.Value,
            MeridianFlip = d.MeridianFlip.Value, FlipAfterHours = d.FlipAfterHours.Value, SiteId = d.SiteId.Text, DitherEvery = d.DitherEvery.Value, DitherPixels = d.DitherPixels.Value,
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
        foreach (var g in _scopeGuiders.Values) await g.DisposeAsync();
        foreach (var t in _trains.Values) await t.Item.DisposeAsync();
        foreach (var s in _shooters.Values) await s.Item.DisposeAsync();
        foreach (var p in _pointers.Values) await p.Item.DisposeAsync();
        _scopes.Clear(); _shooters.Clear(); _pointers.Clear(); _guiders.Clear(); _scopeGuiders.Clear(); _trains.Clear();
    }
}
