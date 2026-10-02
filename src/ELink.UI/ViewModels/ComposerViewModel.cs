using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>A pointer or shooter that can be part of a scope.</summary>
public sealed partial class ComposeChoice : ObservableObject
{
    public ComposeChoice(string id, string origin) { Id = id; Origin = origin; }
    public string Id { get; }
    /// <summary>Where it comes from: "mount Telescope_Simulator", "train 400 mm", "scope", ...</summary>
    public string Origin { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _eastArcmin;
    [ObservableProperty] private double _northArcmin;
    public string Label => Origin == "" ? Id : $"{Id}  ({Origin})";
}

/// <summary>A camera on the mesh, and what it does in the train being composed.</summary>
public sealed partial class TrainCameraChoice : ObservableObject
{
    public TrainCameraChoice(string cameraId) { CameraId = cameraId; }
    public string CameraId { get; }
    [ObservableProperty] private string _role = ComposerViewModel.NotInTrain;
    // for cameras whose driver does not know its sensor (DSLRs); 0 = the camera's own
    [ObservableProperty] private double _pixelSize;
    [ObservableProperty] private int _sensorWidth;
    [ObservableProperty] private int _sensorHeight;
}

/// <summary>Composes the rig: mounts into pointers, optics and cameras into imaging trains, and pointers and trains
/// (any number per mount, plus other scopes) into smart scopes, each with its own guiding. It only calls the
/// composition host's EVent functions.</summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    public const string NotInTrain = "—", NoGuiding = "(no guiding)", MountPort = "(the mount's)";
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
    public ObservableCollection<string> Mounts { get; } = new();
    public ObservableCollection<string> Wheels { get; } = new();
    public ObservableCollection<string> Focusers { get; } = new();
    public ObservableCollection<string> Rotators { get; } = new();
    public ObservableCollection<TrainCameraChoice> TrainCameras { get; } = new();
    public string[] CameraRoles { get; } = [NotInTrain, "Imaging", "Guiding"];
    // what has been composed
    public ObservableCollection<ComposeChoice> PointerChoices { get; } = new();
    public ObservableCollection<ComposeChoice> ShooterChoices { get; } = new();
    public ObservableCollection<string> GuideWith { get; } = new();
    public ObservableCollection<string> GuidePorts { get; } = new();
    public ObservableCollection<string> Existing { get; } = new();
    public string[] GuideOutputs { get; } = ["Pulse", "Correction"];

    // 1: pointer
    [ObservableProperty] private string _newPointerId = "";
    [ObservableProperty] private string? _selectedMount;
    // 2: imaging train
    [ObservableProperty] private string _newTrainId = "";
    [ObservableProperty] private string _trainLabel = "";
    [ObservableProperty] private double _focalLength = 400;
    [ObservableProperty] private double _aperture = 80;
    [ObservableProperty] private string _selectedWheel = "";
    [ObservableProperty] private string _selectedFocuser = "";
    [ObservableProperty] private string _selectedRotator = "";
    // 3: scope and its guiding
    [ObservableProperty] private string _scopeId = "";
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

    [ObservableProperty] private string? _selectedExisting;
    [ObservableProperty] private string _message = "";

    public bool Guided => SelectedGuideWith != NoGuiding;
    public bool PulseOutput => SelectedGuideOutput != "Correction";
    partial void OnSelectedGuideWithChanged(string value) => OnPropertyChanged(nameof(Guided));
    partial void OnSelectedGuideOutputChanged(string value) => OnPropertyChanged(nameof(PulseOutput));

    private void Rebuild()
    {
        UiThread.Post(() =>
        {
            Fill(Mounts, _catalog.OfKind(DeviceKinds.Mount).Select(d => d.Id));
            Fill(Wheels, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.FilterWheel).Select(d => d.Id)));
            Fill(Focusers, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Focuser).Select(d => d.Id)));
            Fill(Rotators, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Rotator).Select(d => d.Id)));
            Fill(GuidePorts, new[] { MountPort }.Concat(_catalog.OfKind(DeviceKinds.GuidePort).Select(d => d.Id)));
            SelectedMount ??= Mounts.FirstOrDefault();
            var cams = _catalog.OfKind(DeviceKinds.Camera).Select(d => d.Id).ToList();
            if (!TrainCameras.Select(c => c.CameraId).SequenceEqual(cams))
            {
                var roles = TrainCameras.ToDictionary(c => c.CameraId, c => c.Role);
                TrainCameras.Clear();
                foreach (var id in cams) TrainCameras.Add(new TrainCameraChoice(id) { Role = roles.GetValueOrDefault(id, NotInTrain) });
            }

            var c = _catalog.Composition;
            var pointers = c.MountPointers.Select(p => (p.Id.Text, $"mount {p.MountId.Text}"))
                .Concat(c.Scopes.Select(s => (s.Id.Text, "scope"))).ToList();
            var shooters = c.Trains.Select(t => (t.Id.Text, $"train{(t.FocalLengthMm.Value > 0 ? $" {t.FocalLengthMm.Value:0} mm" : "")}, {t.Cameras.Count(x => x.Role.Text == "Imaging")} camera(s)"))
                .Concat(c.CameraShooters.Select(s => (s.Id.Text, $"camera {s.CameraId.Text}")))
                .Concat(c.Scopes.Select(s => (s.Id.Text, "scope"))).ToList();
            Refill(PointerChoices, pointers);
            Refill(ShooterChoices, shooters);
            Fill(GuideWith, new[] { NoGuiding }.Concat(_catalog.AllShooters(withScopes: false)));

            var existing = c.MountPointers.Select(p => "Pointer:" + p.Id.Text)
                .Concat(c.Trains.Select(t => "Train:" + t.Id.Text))
                .Concat(c.CameraShooters.Select(s => "Shooter:" + s.Id.Text))
                .Concat(c.Guiders.Select(g => "Guider:" + g.Id.Text))
                .Concat(c.Scopes.Select(s => "Scope:" + s.Id.Text)).ToList();
            Fill(Existing, existing);
        });
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    private static void Refill(ObservableCollection<ComposeChoice> target, List<(string Id, string Origin)> items)
    {
        var old = target.ToDictionary(c => c.Id);
        target.Clear();
        foreach (var (id, origin) in items)
        {
            var choice = new ComposeChoice(id, origin);
            if (old.TryGetValue(id, out var o)) { choice.IsSelected = o.IsSelected; choice.EastArcmin = o.EastArcmin; choice.NorthArcmin = o.NorthArcmin; }
            target.Add(choice);
        }
    }

    private async Task Run(Task<CommandResult> call, string success)
    {
        var r = await call;
        Message = r.Ok.Value ? success : r.Error.Text;
        if (r.Ok.Value) await _catalog.RefreshAsync();
    }

    [RelayCommand]
    private Task DefinePointerAsync()
    {
        if (SelectedMount is null) { Message = "pick a mount first"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = NewPointerId.Trim(), MountId = SelectedMount }), $"pointer '{NewPointerId}' defined");
    }

    [RelayCommand]
    private Task DefineTrainAsync()
    {
        var t = new ImagingTrainDefinition
        {
            Id = NewTrainId.Trim(), Label = TrainLabel.Trim(), FocalLengthMm = FocalLength, ApertureMm = Aperture,
            FilterWheelId = SelectedWheel ?? "", FocuserId = SelectedFocuser ?? "", RotatorId = SelectedRotator ?? "",
        };
        foreach (var c in TrainCameras.Where(c => c.Role != NotInTrain))
            t.Cameras.Add(new TrainCamera { CameraId = c.CameraId, Role = c.Role, PixelSizeUm = c.PixelSize, SensorWidth = c.SensorWidth, SensorHeight = c.SensorHeight });
        if (t.Cameras.Count == 0) { Message = "give at least one camera a role (Imaging, or Guiding for an off-axis guider)"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.DefineTrain, t), $"train '{NewTrainId}' defined");
    }

    [RelayCommand]
    private Task DefineScopeAsync()
    {
        var def = new ScopeDefinition
        {
            Id = ScopeId.Trim(), DisplayName = ScopeName.Trim(), MeridianFlip = MeridianFlip, FlipAfterHours = FlipAfterMinutes / 60, CenterAfterSlew = CenterAfterSlew, CenterToleranceArcmin = CenterTolerance,
            GradeFrames = GradeFrames, FocusOnStart = FocusOnStart, RefocusEveryMinutes = RefocusEveryMinutes, RefocusTemperatureDelta = RefocusTemperature,
            RefocusOnFilterChange = RefocusOnFilter, RefocusHfrIncreasePercent = RefocusHfrPercent, DitherEvery = DitherEvery, DitherPixels = DitherPixels, SettlePixels = SettlePixels,
            GuideShooterId = Guided ? SelectedGuideWith : "", GuideOutput = SelectedGuideOutput, GuideExposureSeconds = GuideExposure,
            GuidePortId = SelectedGuidePort == MountPort ? "" : SelectedGuidePort, GuideTargetId = PulseOutput ? "" : GuideTargetId.Trim(),
        };
        foreach (var p in PointerChoices.Where(c => c.IsSelected)) def.Pointers.Add(p.Id);
        foreach (var s in ShooterChoices.Where(c => c.IsSelected))
            def.Shooters.Add(new ScopeShooterRef { Id = s.Id, OffsetEastArcmin = s.EastArcmin, OffsetNorthArcmin = s.NorthArcmin });
        if (def.Pointers.Count == 0 && def.Shooters.Count == 0) { Message = "select at least one pointer or train"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.Define, def), $"scope '{ScopeId}' defined");
    }

    [RelayCommand]
    private Task RemoveAsync()
    {
        if (SelectedExisting is null) return Task.CompletedTask;
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.Remove, (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)SelectedExisting), $"removed {SelectedExisting}");
    }
}
