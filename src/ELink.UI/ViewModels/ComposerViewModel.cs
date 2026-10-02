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
    /// <summary>Where it comes from: "mount Telescope_Simulator", "scope Wide", ...</summary>
    public string Origin { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _eastArcmin;
    [ObservableProperty] private double _northArcmin;
    public string Label => Origin == "" ? Id : $"{Id}  ({Origin})";
}

/// <summary>Composes smart scopes: turn mounts into pointers, cameras (with wheels) into shooters, and pick pointers and
/// shooters (including other scopes) for a scope. It only calls the composition host's EVent functions.</summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;

    public ComposerViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => Rebuild();
        catalog.Devices.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<string> Mounts { get; } = new();
    public ObservableCollection<string> Cameras { get; } = new();
    public ObservableCollection<string> Wheels { get; } = new();
    public ObservableCollection<ComposeChoice> PointerChoices { get; } = new();
    public ObservableCollection<ComposeChoice> ShooterChoices { get; } = new();
    public ObservableCollection<string> Existing { get; } = new();
    public ObservableCollection<string> GuideCameras { get; } = new();
    public ObservableCollection<string> GuidePorts { get; } = new();
    public ObservableCollection<string> GuideMounts { get; } = new();
    public ObservableCollection<string> Guiders { get; } = new();
    public string[] GuideOutputs { get; } = ["Pulse", "Correction"];
    public const string NoGuider = "(none)";

    [ObservableProperty] private string _newGuiderId = "";
    [ObservableProperty] private string? _selectedGuideCamera;
    [ObservableProperty] private string _selectedGuideOutput = "Pulse";
    [ObservableProperty] private string? _selectedGuidePort;
    [ObservableProperty] private string _guideTargetId = "";
    [ObservableProperty] private string? _selectedGuideMount;
    [ObservableProperty] private double _guideExposure = 2;
    [ObservableProperty] private double _guideScale;
    [ObservableProperty] private string _selectedGuider = NoGuider;
    [ObservableProperty] private int _ditherEvery = 3;
    [ObservableProperty] private double _ditherPixels = 5;
    [ObservableProperty] private double _settlePixels = 1.5;

    public bool PulseOutput => SelectedGuideOutput != "Correction";
    partial void OnSelectedGuideOutputChanged(string value) => OnPropertyChanged(nameof(PulseOutput));

    [ObservableProperty] private string _newPointerId = "";
    [ObservableProperty] private string? _selectedMount;
    [ObservableProperty] private string _newShooterId = "";
    [ObservableProperty] private string? _selectedCamera;
    [ObservableProperty] private string? _selectedWheel;
    [ObservableProperty] private string _scopeId = "";
    [ObservableProperty] private string _scopeName = "";
    [ObservableProperty] private string? _selectedExisting;
    [ObservableProperty] private string _message = "";

    private void Rebuild()
    {
        UiThread.Post(() =>
        {
            Fill(Mounts, _catalog.OfKind(DeviceKinds.Mount).Select(d => d.Id));
            Fill(Cameras, _catalog.OfKind(DeviceKinds.Camera).Select(d => d.Id));
            Fill(Wheels, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.FilterWheel).Select(d => d.Id)));
            SelectedMount ??= Mounts.FirstOrDefault();
            SelectedCamera ??= Cameras.FirstOrDefault();
            SelectedWheel ??= "";

            var c = _catalog.Composition;
            var pointers = c.MountPointers.Select(p => (p.Id.Text, $"mount {p.MountId.Text}"))
                .Concat(c.Scopes.Select(s => (s.Id.Text, "scope"))).ToList();
            var shooters = c.CameraShooters.Select(s => (s.Id.Text, s.FilterWheelId.Text == "" ? $"camera {s.CameraId.Text}" : $"camera {s.CameraId.Text} + wheel"))
                .Concat(c.Scopes.Select(s => (s.Id.Text, "scope"))).ToList();
            Fill(GuideCameras, c.CameraShooters.Select(x => x.Id.Text));
            Fill(GuidePorts, _catalog.OfKind(DeviceKinds.GuidePort).Select(d => d.Id));
            Fill(GuideMounts, new[] { "" }.Concat(_catalog.OfKind(DeviceKinds.Mount).Select(d => d.Id)));
            Fill(Guiders, new[] { NoGuider }.Concat(c.Guiders.Select(g => g.Id.Text)));
            SelectedGuideCamera ??= GuideCameras.FirstOrDefault();
            SelectedGuidePort ??= GuidePorts.FirstOrDefault();
            SelectedGuideMount ??= GuideMounts.Skip(1).FirstOrDefault() ?? "";
            Refill(PointerChoices, pointers);
            Refill(ShooterChoices, shooters);

            var existing = c.MountPointers.Select(p => "Pointer:" + p.Id.Text)
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
    private Task DefineShooterAsync()
    {
        if (SelectedCamera is null) { Message = "pick a camera first"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.DefineCameraShooter,
            new CameraShooterDefinition { Id = NewShooterId.Trim(), CameraId = SelectedCamera, FilterWheelId = SelectedWheel ?? "" }), $"shooter '{NewShooterId}' defined");
    }

    [RelayCommand]
    private Task DefineGuiderAsync()
    {
        if (SelectedGuideCamera is null) { Message = "define a shooter for the guide camera first"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.DefineGuider, new GuiderDefinition
        {
            Id = NewGuiderId.Trim(), ShooterId = SelectedGuideCamera, Output = SelectedGuideOutput, GuidePortId = PulseOutput ? SelectedGuidePort ?? "" : "",
            TargetId = PulseOutput ? "" : GuideTargetId.Trim(), MountId = SelectedGuideMount ?? "", ExposureSeconds = GuideExposure, PixelScaleArcsec = GuideScale,
        }), $"guider '{NewGuiderId}' defined");
    }

    [RelayCommand]
    private Task DefineScopeAsync()
    {
        var def = new ScopeDefinition
        {
            Id = ScopeId.Trim(), DisplayName = ScopeName.Trim(), GuiderId = SelectedGuider == NoGuider ? "" : SelectedGuider,
            DitherEvery = DitherEvery, DitherPixels = DitherPixels, SettlePixels = SettlePixels,
        };
        foreach (var p in PointerChoices.Where(c => c.IsSelected)) def.Pointers.Add(p.Id);
        foreach (var s in ShooterChoices.Where(c => c.IsSelected))
            def.Shooters.Add(new ScopeShooterRef { Id = s.Id, OffsetEastArcmin = s.EastArcmin, OffsetNorthArcmin = s.NorthArcmin });
        if (def.Pointers.Count == 0 && def.Shooters.Count == 0) { Message = "select at least one pointer or shooter"; return Task.CompletedTask; }
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.Define, def), $"scope '{ScopeId}' defined");
    }

    [RelayCommand]
    private Task RemoveAsync()
    {
        if (SelectedExisting is null) return Task.CompletedTask;
        return Run(Commands.CallAsync(_mesh.Node, ScopeIds.Remove, (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)SelectedExisting), $"removed {SelectedExisting}");
    }
}
