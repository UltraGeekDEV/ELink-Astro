using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.UI.Infrastructure;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>A mount, another scope or a telescope that can be part of a scope.</summary>
public sealed partial class ComposeChoice : ObservableObject
{
    public ComposeChoice(string id, string title, string origin = "", bool isNew = false) { Id = id; Title = title; Origin = origin; IsNew = isNew; }
    public string Id { get; }
    public string Title { get; }
    /// <summary>What it is, in a few words ("400 mm, 1 camera").</summary>
    public string Origin { get; }
    /// <summary>A mount that has no pointer yet: one is made when the scope is saved.</summary>
    public bool IsNew { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _eastArcmin;
    [ObservableProperty] private double _northArcmin;
    public string Label => Origin == "" ? Title : $"{Title}  ·  {Origin}";
}

/// <summary>A camera on the mesh, and what it does in the telescope being set up.</summary>
public sealed partial class TrainCameraChoice : ObservableObject
{
    public TrainCameraChoice(string cameraId, string name) { CameraId = cameraId; Name = name; }
    public string CameraId { get; }
    public string Name { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsUsed))] private string _role = ComposerViewModel.NotInTrain;
    public bool IsUsed => Role != ComposerViewModel.NotInTrain;
    // for cameras whose driver does not know its sensor (DSLRs); 0 = the camera's own
    [ObservableProperty] private double _pixelSize;
    [ObservableProperty] private int _sensorWidth;
    [ObservableProperty] private int _sensorHeight;
    // presets: empty = leave the camera's own
    [ObservableProperty] private string _gain = "";
    [ObservableProperty] private string _offset = "";
    // cooling set point °C: empty = no cooling
    [ObservableProperty] private string _coolTo = "";
    [ObservableProperty] private double _coolRate = 3;
    // a colour camera that takes turns to focus its red, green and blue (focus offsets named R, G, B)
    [ObservableProperty] private bool _pseudoMono;

    public static double Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    public static string Text(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A line in a list of what is set up: a title, a few words about it, and what it is called to the backend.</summary>
public sealed record SetupItem(string Id, string Title, string Detail);

/// <summary>A choice of what a scope guides with.</summary>
public sealed record GuideOption(string Id, string Label);

/// <summary>Setting up the rig: telescopes (optics, cameras, filter wheel, focuser) and scopes (mounts, telescopes, guiding and
/// the rest of what a scope does by itself). Mounts get their pointers made for them. Everything that is set up can be changed:
/// it only calls the composition host's EVent functions.</summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    public const string NotInTrain = "Not used", NoGuiding = "none", MountPort = "(the mount's)";
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;

    public ComposerViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => Rebuild();
        catalog.Devices.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    // devices on the mesh
    public ObservableCollection<string> Wheels { get; } = new();
    public ObservableCollection<string> Focusers { get; } = new();
    public ObservableCollection<string> Rotators { get; } = new();
    public ObservableCollection<TrainCameraChoice> TrainCameras { get; } = new();
    public string[] CameraRoles { get; } = [NotInTrain, "Imaging", "Guiding"];
    public string[] GuideOutputs { get; } = ["Pulse", "Correction"];
    /// <summary>Shown for "no device" in the wheel, focuser and rotator lists.</summary>
    public const string None = "";

    // what is set up
    public ObservableCollection<SetupItem> Trains { get; } = new();
    public ObservableCollection<SetupItem> Scopes { get; } = new();
    public ObservableCollection<SetupItem> Pointers { get; } = new();
    public bool HasTrains => Trains.Count > 0;
    public bool HasScopes => Scopes.Count > 0;
    public bool HasPointers => Pointers.Count > 0;
    public bool NoTrains => Trains.Count == 0;
    public bool NoScopes => Scopes.Count == 0;

    // the scope form's choices
    public ObservableCollection<ComposeChoice> PointerChoices { get; } = new();     // mounts
    public ObservableCollection<ComposeChoice> ShooterChoices { get; } = new();     // telescopes
    public ObservableCollection<ComposeChoice> NestedChoices { get; } = new();      // other scopes, to combine
    public ObservableCollection<GuideOption> GuideWith { get; } = new();
    public ObservableCollection<string> GuidePorts { get; } = new();

    // ---- telescope (imaging train) form ----------------------------------------------------------------------------
    [ObservableProperty] private bool _trainFormOpen;
    [ObservableProperty] private string? _trainEditingId;
    [ObservableProperty] private string _trainName = "";
    [ObservableProperty] private double _focalLength = 400;
    [ObservableProperty] private double _aperture = 80;
    [ObservableProperty] private string _focusOffsets = "";
    [ObservableProperty] private string _selectedWheel = "";
    [ObservableProperty] private string _selectedFocuser = "";
    [ObservableProperty] private string _selectedRotator = "";
    [ObservableProperty] private string _trainMessage = "";
    [ObservableProperty] private string _trainMessageKind = "error";
    public bool TrainIsNew => TrainEditingId is null;
    public string TrainFormTitle => TrainEditingId is null ? "New telescope" : $"Telescope: {TrainEditingId}";
    public bool CanSetFocusOffsets => SelectedWheel != "" && SelectedFocuser != "";
    partial void OnTrainEditingIdChanged(string? value) { OnPropertyChanged(nameof(TrainIsNew)); OnPropertyChanged(nameof(TrainFormTitle)); }
    partial void OnSelectedWheelChanged(string value) => OnPropertyChanged(nameof(CanSetFocusOffsets));
    partial void OnSelectedFocuserChanged(string value) => OnPropertyChanged(nameof(CanSetFocusOffsets));

    // ---- scope form --------------------------------------------------------------------------------------------------
    [ObservableProperty] private bool _scopeFormOpen;
    [ObservableProperty] private string? _scopeEditingId;
    [ObservableProperty] private string _scopeName = "";
    [ObservableProperty] private string _selectedGuideWith = NoGuiding;
    [ObservableProperty] private string _selectedGuideOutput = "Pulse";
    [ObservableProperty] private string _selectedGuidePort = MountPort;
    [ObservableProperty] private string _guideTargetId = "";
    [ObservableProperty] private double _guideExposure = 2;
    [ObservableProperty] private int _ditherEvery = 3;
    [ObservableProperty] private double _ditherPixels = 5;
    [ObservableProperty] private double _settlePixels = 1.5;
    [ObservableProperty] private bool _meridianFlip = true;
    [ObservableProperty] private bool _centerAfterSlew;
    [ObservableProperty] private double _centerTolerance = 1;
    [ObservableProperty] private bool _focusOnStart;
    [ObservableProperty] private bool _gradeFrames = true;
    [ObservableProperty] private double _refocusEveryMinutes;
    [ObservableProperty] private double _refocusTemperature;
    [ObservableProperty] private bool _refocusOnFilter;
    [ObservableProperty] private double _refocusHfrPercent;
    [ObservableProperty] private double _flipAfterMinutes = 6;
    [ObservableProperty] private string _scopeMessage = "";
    [ObservableProperty] private string _scopeMessageKind = "error";
    public bool ScopeIsNew => ScopeEditingId is null;
    public string ScopeFormTitle => ScopeEditingId is null ? "New scope" : $"Scope: {ScopeEditingId}";
    public bool Guided => SelectedGuideWith != NoGuiding;
    public bool PulseOutput => SelectedGuideOutput != "Correction";
    partial void OnScopeEditingIdChanged(string? value) { OnPropertyChanged(nameof(ScopeIsNew)); OnPropertyChanged(nameof(ScopeFormTitle)); }
    partial void OnSelectedGuideWithChanged(string value) => OnPropertyChanged(nameof(Guided));
    partial void OnSelectedGuideOutputChanged(string value) => OnPropertyChanged(nameof(PulseOutput));

    // ---- pointers (advanced) -----------------------------------------------------------------------------------------
    [ObservableProperty] private string _newPointerId = "";
    [ObservableProperty] private string? _selectedMount;
    public ObservableCollection<string> Mounts { get; } = new();
    [ObservableProperty] private string _pointerMessage = "";
    [ObservableProperty] private string _pointerMessageKind = "error";

    // ---- keeping the lists current -------------------------------------------------------------------------------------

    private string NameOf(string kind, string id) => _catalog.Devices.FirstOrDefault(d => d.Kind == kind && d.Id == id)?.DisplayName ?? id;

    private void Rebuild()
    {
        UiThread.Post(() =>
        {
            Fill(Mounts, _catalog.OfKind(DeviceKinds.Mount).Select(d => d.Id));
            Fill(Wheels, new[] { None }.Concat(_catalog.OfKind(DeviceKinds.FilterWheel).Select(d => d.Id)));
            Fill(Focusers, new[] { None }.Concat(_catalog.OfKind(DeviceKinds.Focuser).Select(d => d.Id)));
            Fill(Rotators, new[] { None }.Concat(_catalog.OfKind(DeviceKinds.Rotator).Select(d => d.Id)));
            Fill(GuidePorts, new[] { MountPort }.Concat(_catalog.OfKind(DeviceKinds.GuidePort).Select(d => d.Id)));
            SelectedMount ??= Mounts.FirstOrDefault();
            var cams = _catalog.OfKind(DeviceKinds.Camera).Select(d => d.Id).ToList();
            if (!TrainCameras.Select(c => c.CameraId).SequenceEqual(cams))
            {
                var old = TrainCameras.ToDictionary(c => c.CameraId);
                TrainCameras.Clear();
                foreach (var id in cams) TrainCameras.Add(old.TryGetValue(id, out var o) ? o : new TrainCameraChoice(id, NameOf(DeviceKinds.Camera, id)));
            }

            var c = _catalog.Composition;
            RefillItems(Trains, c.Trains.Select(t => new SetupItem(t.Id.Text, t.Label.Text != "" ? t.Label.Text : t.Id.Text, TrainDetail(t))));
            RefillItems(Scopes, c.Scopes.Select(s => new SetupItem(s.Id.Text, s.DisplayName.Text != "" ? s.DisplayName.Text : s.Id.Text, ScopeDetail(s))));
            RefillItems(Pointers, c.MountPointers.Select(p => new SetupItem(p.Id.Text, p.Id.Text, $"points the mount {NameOf(DeviceKinds.Mount, p.MountId.Text)}")));
            foreach (var n in new[] { nameof(HasTrains), nameof(HasScopes), nameof(HasPointers), nameof(NoTrains), nameof(NoScopes) }) OnPropertyChanged(n);

            // mounts a scope can be built on: ones with a pointer already, ones without (one is made when the scope is saved)
            var mountChoices = new List<(string Id, string Title, string Origin, bool IsNew)>();
            foreach (var m in _catalog.OfKind(DeviceKinds.Mount))
            {
                var existing = c.MountPointers.FirstOrDefault(p => p.MountId.Text == m.Id);
                mountChoices.Add(existing is not null ? (existing.Id.Text, m.DisplayName, "", false) : (IdFromName(m.Id) ?? m.Id, m.DisplayName, "", true));
            }
            foreach (var p in c.MountPointers.Where(p => mountChoices.All(x => x.Id != p.Id.Text)))
                mountChoices.Add((p.Id.Text, NameOf(DeviceKinds.Mount, p.MountId.Text), "not connected", false));
            Refill(PointerChoices, mountChoices);
            Refill(ShooterChoices, c.Trains.Select(t => (t.Id.Text, t.Label.Text != "" ? t.Label.Text : t.Id.Text, TrainDetail(t), false))
                .Concat(c.CameraShooters.Select(s => (s.Id.Text, s.Id.Text, $"camera {NameOf(DeviceKinds.Camera, s.CameraId.Text)}", false))).ToList());
            Refill(NestedChoices, c.Scopes.Select(s => (s.Id.Text, s.DisplayName.Text != "" ? s.DisplayName.Text : s.Id.Text, "combine its mounts and telescopes", false)).ToList());

            var guide = new List<GuideOption> { new(NoGuiding, "No guiding") };
            foreach (var t in c.Trains)
            {
                guide.Add(new GuideOption(t.Id.Text, $"Guide with the telescope {(t.Label.Text != "" ? t.Label.Text : t.Id.Text)}"));
                if (t.Cameras.Any(x => x.Role.Text == "Guiding")) guide.Add(new GuideOption(TrainIds.GuideShooter(t.Id.Text), $"Guide with the off-axis guider of {(t.Label.Text != "" ? t.Label.Text : t.Id.Text)}"));
            }
            foreach (var s in c.CameraShooters) guide.Add(new GuideOption(s.Id.Text, $"Guide with the camera {s.Id.Text}"));
            if (!GuideWith.Select(g => g.Id).SequenceEqual(guide.Select(g => g.Id)))
            {
                GuideWith.Clear(); foreach (var g in guide) GuideWith.Add(g);
                OnPropertyChanged(nameof(SelectedGuideWith));      // the list was empty when the selection was bound: look again
            }
        });
    }

    private string TrainDetail(ImagingTrainDefinition t)
    {
        var parts = new List<string>();
        if (t.FocalLengthMm.Value > 0) parts.Add($"{t.FocalLengthMm.Value:0} mm");
        foreach (var cam in t.Cameras) parts.Add($"{NameOf(DeviceKinds.Camera, cam.CameraId.Text)} ({(cam.Role.Text == "Guiding" ? "off-axis guider" : "imaging")})");
        if (t.FilterWheelId.Text != "") parts.Add("filter wheel");
        if (t.FocuserId.Text != "") parts.Add("focuser");
        return string.Join("  ·  ", parts);
    }

    private string ScopeDetail(ScopeDefinition s)
    {
        var parts = new List<string>();
        if (s.Pointers.Count > 0) parts.Add(string.Join(" + ", s.Pointers.Select(p => PointerTitle(p.Text))));
        if (s.Shooters.Count > 0) parts.Add(string.Join(" + ", s.Shooters.Select(x => x.Id.Text)));
        parts.Add(s.GuideShooterId.Text != "" || s.GuiderId.Text != "" ? "guides itself" : "no guiding");
        return string.Join("  ·  ", parts);
    }

    private string PointerTitle(string pointerId) =>
        _catalog.Composition.MountPointers.FirstOrDefault(p => p.Id.Text == pointerId) is { } p ? NameOf(DeviceKinds.Mount, p.MountId.Text) : pointerId;

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    private static void RefillItems(ObservableCollection<SetupItem> target, IEnumerable<SetupItem> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    private static void Refill(ObservableCollection<ComposeChoice> target, List<(string Id, string Title, string Origin, bool IsNew)> items)
    {
        var old = target.ToDictionary(c => c.Id);
        target.Clear();
        foreach (var (id, title, origin, isNew) in items)
        {
            var choice = new ComposeChoice(id, title, origin, isNew);
            if (old.TryGetValue(id, out var o)) { choice.IsSelected = o.IsSelected; choice.EastArcmin = o.EastArcmin; choice.NorthArcmin = o.NorthArcmin; }
            target.Add(choice);
        }
    }

    /// <summary>A name made into an id: letters, digits, '-' and '_' only. Null when nothing is left.</summary>
    public static string? IdFromName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var s = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return s == "" ? null : s;
    }

    private async Task<CommandResult> Call<T>(string id, T input) where T : IBinaryConvertible, new()
    {
        var r = await Commands.CallAsync(_mesh.Node, id, input);
        if (r.Ok.Value) await _catalog.RefreshAsync();
        return r;
    }

    private static T Clone<T>(T value) where T : IBinaryConvertible, new()
    {
        var copy = new T();
        Span<byte> bytes = value.ToBytes();
        copy.FromBytes(ref bytes);
        return copy;
    }

    // ---- telescopes ----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewTrain()
    {
        TrainEditingId = null; TrainName = ""; FocalLength = 400; Aperture = 80; FocusOffsets = "";
        SelectedWheel = SelectedFocuser = SelectedRotator = None;
        foreach (var c in TrainCameras) { c.Role = NotInTrain; c.PixelSize = 0; c.SensorWidth = c.SensorHeight = 0; c.Gain = c.Offset = c.CoolTo = ""; c.CoolRate = 3; c.PseudoMono = false; }
        TrainMessage = ""; TrainFormOpen = true;
    }

    [RelayCommand]
    private void EditTrain(SetupItem? item)
    {
        var t = item is null ? null : _catalog.Composition.Trains.FirstOrDefault(x => x.Id.Text == item.Id);
        if (t is null) return;
        NewTrain();
        TrainEditingId = t.Id.Text; TrainName = t.Label.Text != "" ? t.Label.Text : t.Id.Text; FocalLength = t.FocalLengthMm.Value; Aperture = t.ApertureMm.Value;
        SelectedWheel = t.FilterWheelId.Text; SelectedFocuser = t.FocuserId.Text; SelectedRotator = t.RotatorId.Text;
        FocusOffsets = string.Join(", ", t.FocusOffsets.Select(o => $"{o.Filter.Text}={o.Steps.Value}"));
        foreach (var cam in t.Cameras)
        {
            var choice = TrainCameras.FirstOrDefault(c => c.CameraId == cam.CameraId.Text);
            if (choice is null) { choice = new TrainCameraChoice(cam.CameraId.Text, NameOf(DeviceKinds.Camera, cam.CameraId.Text)); TrainCameras.Add(choice); }
            choice.Role = cam.Role.Text; choice.PixelSize = cam.PixelSizeUm.Value; choice.SensorWidth = cam.SensorWidth.Value; choice.SensorHeight = cam.SensorHeight.Value;
            choice.Gain = TrainCameraChoice.Text(cam.Gain.Value); choice.Offset = TrainCameraChoice.Text(cam.Offset.Value); choice.CoolTo = TrainCameraChoice.Text(cam.CoolTo.Value); choice.CoolRate = cam.CoolDegreesPerMinute.Value; choice.PseudoMono = cam.PseudoMono.Value;
        }
    }

    [RelayCommand] private void CancelTrain() { TrainFormOpen = false; TrainMessage = ""; }

    private void TrainSays(string text, string kind = "error") { TrainMessageKind = kind; TrainMessage = text; }

    [RelayCommand]
    private async Task SaveTrainAsync()
    {
        string? id = TrainEditingId ?? IdFromName(TrainName);
        if (id is null) { TrainSays("give the telescope a name"); return; }
        if (TrainEditingId is null && _catalog.Composition.Trains.Any(t => t.Id.Text == id)) { TrainSays($"a telescope called '{id}' exists: pick another name, or edit that one"); return; }
        var t = new ImagingTrainDefinition
        {
            Id = id, Label = TrainName.Trim(), FocalLengthMm = FocalLength, ApertureMm = Aperture,
            FilterWheelId = SelectedWheel ?? "", FocuserId = SelectedFocuser ?? "", RotatorId = SelectedRotator ?? "",
        };
        foreach (var c in TrainCameras.Where(c => c.IsUsed))
            t.Cameras.Add(new TrainCamera
            {
                CameraId = c.CameraId, Role = c.Role, PixelSizeUm = c.PixelSize, SensorWidth = c.SensorWidth, SensorHeight = c.SensorHeight,
                Gain = TrainCameraChoice.Number(c.Gain), Offset = TrainCameraChoice.Number(c.Offset), CoolTo = TrainCameraChoice.Number(c.CoolTo), CoolDegreesPerMinute = Math.Clamp(c.CoolRate, 0.1, 30), PseudoMono = c.PseudoMono && c.Role == "Imaging",
            });
        // "L=0, R=30, Ha=120": focuser steps per filter
        foreach (var part in FocusOffsets.Split(',', ';').Select(p => p.Trim()).Where(p => p != ""))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), out int steps)) { TrainSays($"focus offsets are like L=0, R=30, Ha=120 (not '{part}')"); return; }
            t.FocusOffsets.Add(new FilterFocusOffset { Filter = kv[0].Trim(), Steps = steps });
        }
        if (t.Cameras.Count == 0) { TrainSays("choose at least one camera: Imaging, or Guiding for an off-axis guider"); return; }
        var r = await Call(ScopeIds.DefineTrain, t);
        if (!r.Ok.Value) { TrainSays(r.Error.Text); return; }
        TrainFormOpen = false; TrainEditingId = null;
        _mesh.Notices.Success($"Telescope '{TrainName.Trim()}' saved", "Set up");
    }

    [RelayCommand]
    private async Task RemoveTrainAsync(SetupItem? item)
    {
        if (item is null) return;
        var r = await Call(ScopeIds.Remove, (BinaryConvertibleString)("Train:" + item.Id));
        if (!r.Ok.Value) _mesh.Notices.Error($"{item.Title}: {r.Error.Text}", "Set up"); else _mesh.Notices.Info($"Telescope '{item.Title}' removed", "Set up");
    }

    // ---- scopes --------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewScope()
    {
        ScopeEditingId = null; ScopeName = "";
        foreach (var c in PointerChoices.Concat(ShooterChoices).Concat(NestedChoices)) { c.IsSelected = false; c.EastArcmin = c.NorthArcmin = 0; }
        // the obvious first scope: the one mount and the one telescope, when there is just one of each
        if (PointerChoices.Count == 1) PointerChoices[0].IsSelected = true;
        if (ShooterChoices.Count == 1) ShooterChoices[0].IsSelected = true;
        SelectedGuideWith = NoGuiding; SelectedGuideOutput = "Pulse"; SelectedGuidePort = MountPort; GuideTargetId = ""; GuideExposure = 2;
        DitherEvery = 3; DitherPixels = 5; SettlePixels = 1.5; MeridianFlip = true; FlipAfterMinutes = 6; CenterAfterSlew = false; CenterTolerance = 1; GradeFrames = true;
        FocusOnStart = false; RefocusEveryMinutes = 0; RefocusTemperature = 0; RefocusOnFilter = false; RefocusHfrPercent = 0;
        ScopeMessage = ""; ScopeFormOpen = true;
    }

    [RelayCommand]
    private void EditScope(SetupItem? item)
    {
        var s = item is null ? null : _catalog.Composition.Scopes.FirstOrDefault(x => x.Id.Text == item.Id);
        if (s is null) return;
        NewScope();
        ScopeEditingId = s.Id.Text; ScopeName = s.DisplayName.Text != "" ? s.DisplayName.Text : s.Id.Text;
        foreach (var c in PointerChoices.Concat(NestedChoices)) c.IsSelected = s.Pointers.Any(p => p.Text == c.Id);
        foreach (var c in ShooterChoices.Concat(NestedChoices)) if (s.Shooters.FirstOrDefault(x => x.Id.Text == c.Id) is { } r) { c.IsSelected = true; c.EastArcmin = r.OffsetEastArcmin.Value; c.NorthArcmin = r.OffsetNorthArcmin.Value; }
        SelectedGuideWith = s.GuideShooterId.Text != "" ? s.GuideShooterId.Text : NoGuiding;
        SelectedGuideOutput = s.GuideOutput.Text; SelectedGuidePort = s.GuidePortId.Text == "" ? MountPort : s.GuidePortId.Text; GuideTargetId = s.GuideTargetId.Text; GuideExposure = s.GuideExposureSeconds.Value;
        DitherEvery = s.DitherEvery.Value; DitherPixels = s.DitherPixels.Value; SettlePixels = s.SettlePixels.Value;
        MeridianFlip = s.MeridianFlip.Value; FlipAfterMinutes = s.FlipAfterHours.Value * 60; CenterAfterSlew = s.CenterAfterSlew.Value; CenterTolerance = s.CenterToleranceArcmin.Value; GradeFrames = s.GradeFrames.Value;
        FocusOnStart = s.FocusOnStart.Value; RefocusEveryMinutes = s.RefocusEveryMinutes.Value; RefocusTemperature = s.RefocusTemperatureDelta.Value;
        RefocusOnFilter = s.RefocusOnFilterChange.Value; RefocusHfrPercent = s.RefocusHfrIncreasePercent.Value;
    }

    [RelayCommand] private void CancelScope() { ScopeFormOpen = false; ScopeMessage = ""; }

    private void ScopeSays(string text, string kind = "error") { ScopeMessageKind = kind; ScopeMessage = text; }

    [RelayCommand]
    private async Task SaveScopeAsync()
    {
        string? id = ScopeEditingId ?? IdFromName(ScopeName);
        if (id is null) { ScopeSays("give the scope a name"); return; }
        if (ScopeEditingId is null && _catalog.Composition.Scopes.Any(s => s.Id.Text == id)) { ScopeSays($"a scope called '{id}' exists: pick another name, or edit that one"); return; }
        var mounts = PointerChoices.Where(c => c.IsSelected).ToList();
        var trains = ShooterChoices.Where(c => c.IsSelected).ToList();
        var nested = NestedChoices.Where(c => c.IsSelected).ToList();
        if (mounts.Count == 0 && nested.Count == 0) { ScopeSays("choose the mount this scope points"); return; }
        if (trains.Count == 0 && nested.Count == 0) { ScopeSays("choose at least one telescope for this scope"); return; }
        // keep what the form does not show (focus settings, centring camera, ...) when editing
        var existing = ScopeEditingId is null ? null : _catalog.Composition.Scopes.FirstOrDefault(s => s.Id.Text == ScopeEditingId);
        var def = existing is null ? new ScopeDefinition() : Clone(existing);
        def.Id = id; def.DisplayName = ScopeName.Trim();
        def.MeridianFlip = MeridianFlip; def.FlipAfterHours = FlipAfterMinutes / 60; def.CenterAfterSlew = CenterAfterSlew; def.CenterToleranceArcmin = CenterTolerance;
        def.GradeFrames = GradeFrames; def.FocusOnStart = FocusOnStart; def.RefocusEveryMinutes = RefocusEveryMinutes; def.RefocusTemperatureDelta = RefocusTemperature;
        def.RefocusOnFilterChange = RefocusOnFilter; def.RefocusHfrIncreasePercent = RefocusHfrPercent; def.DitherEvery = DitherEvery; def.DitherPixels = DitherPixels; def.SettlePixels = SettlePixels;
        def.GuideShooterId = Guided ? SelectedGuideWith : ""; def.GuideOutput = SelectedGuideOutput; def.GuideExposureSeconds = GuideExposure;
        def.GuidePortId = SelectedGuidePort == MountPort ? "" : SelectedGuidePort; def.GuideTargetId = PulseOutput ? "" : GuideTargetId.Trim();
        def.Pointers = new(); def.Shooters = new();
        foreach (var m in mounts)
        {
            if (m.IsNew)
            {
                // a mount points through a pointer: make it (named after the mount)
                string mountId = _catalog.OfKind(DeviceKinds.Mount).FirstOrDefault(d => IdFromName(d.Id) == m.Id || d.Id == m.Id)?.Id ?? m.Id;
                var pr = await Call(ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = m.Id, MountId = mountId });
                if (!pr.Ok.Value) { ScopeSays($"{m.Title}: {pr.Error.Text}"); return; }
            }
            def.Pointers.Add(m.Id);
        }
        foreach (var n in nested) { def.Pointers.Add(n.Id); def.Shooters.Add(new ScopeShooterRef { Id = n.Id }); }
        foreach (var t in trains) def.Shooters.Add(new ScopeShooterRef { Id = t.Id, OffsetEastArcmin = t.EastArcmin, OffsetNorthArcmin = t.NorthArcmin });
        var r = await Call(ScopeIds.Define, def);
        if (!r.Ok.Value) { ScopeSays(r.Error.Text); return; }
        ScopeFormOpen = false; ScopeEditingId = null;
        _mesh.Notices.Success($"Scope '{ScopeName.Trim()}' saved: it is under Scopes now", "Set up");
    }

    [RelayCommand]
    private async Task RemoveScopeAsync(SetupItem? item)
    {
        if (item is null) return;
        var r = await Call(ScopeIds.Remove, (BinaryConvertibleString)("Scope:" + item.Id));
        if (!r.Ok.Value) _mesh.Notices.Error($"{item.Title}: {r.Error.Text}", "Set up"); else _mesh.Notices.Info($"Scope '{item.Title}' removed", "Set up");
    }

    // ---- pointers (advanced) ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task DefinePointerAsync()
    {
        string? id = IdFromName(NewPointerId);
        if (SelectedMount is null) { PointerMessageKind = "error"; PointerMessage = "pick a mount first"; return; }
        if (id is null) { PointerMessageKind = "error"; PointerMessage = "give the pointer a name"; return; }
        var r = await Call(ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = id, MountId = SelectedMount });
        PointerMessageKind = r.Ok.Value ? "ok" : "error"; PointerMessage = r.Ok.Value ? $"pointer '{id}' defined" : r.Error.Text;
    }

    [RelayCommand]
    private async Task RemovePointerAsync(SetupItem? item)
    {
        if (item is null) return;
        var r = await Call(ScopeIds.Remove, (BinaryConvertibleString)("Pointer:" + item.Id));
        PointerMessageKind = r.Ok.Value ? "ok" : "error"; PointerMessage = r.Ok.Value ? $"pointer '{item.Id}' removed" : r.Error.Text;
    }
}
