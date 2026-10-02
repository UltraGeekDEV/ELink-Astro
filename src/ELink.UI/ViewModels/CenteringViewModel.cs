using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>Plate solving and automatic centring: pick the mount and the guide and primary cameras, learn their offset, and let
/// every slew (also from a hand controller) be centred on the primary.</summary>
public sealed partial class CenteringViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<CenteringState>? _follower;
    private Action<SolveResult>? _solvedHook;
    private bool _loaded;

    public CenteringViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        catalog.Devices.CollectionChanged += (_, _) => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Mounts { get; } = new();
    public ObservableCollection<string> Shooters { get; } = new();
    public ObservableCollection<string> Solves { get; } = new();
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _mountId;
    [ObservableProperty] private string? _guideShooter;
    [ObservableProperty] private string? _primaryShooter;
    [ObservableProperty] private double _exposureSeconds = 2;
    [ObservableProperty] private double _toleranceArcmin = 1;
    [ObservableProperty] private int _maxIterations = 5;
    [ObservableProperty] private bool _useOffset = true;
    [ObservableProperty] private bool _syncMount = true;

    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _stateMessage = "";
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _offsetText = "no offset learned yet";
    [ObservableProperty] private string _message = "";

    private void RebuildChoices()
    {
        Fill(Mounts, _catalog.OfKind(DeviceKinds.Mount).Select(d => d.Id));
        var c = _catalog.Composition;
        Fill(Shooters, _catalog.AllShooters());
        MountId ??= Mounts.FirstOrDefault();
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    public async Task StartAsync()
    {
        _follower = new Follower<CenteringState>(_mesh.Node, CenteringIds.State, CenteringIds.GetState, s =>
        {
            Phase = s.Phase.Text; StateMessage = s.Message.Text;
            TargetText = double.IsNaN(s.TargetRaHours.Value) ? "" :
                $"target {Sexagesimal.Format(s.TargetRaHours.Value)} {Sexagesimal.Format(s.TargetDecDegrees.Value, 0)}" +
                (double.IsNaN(s.ErrorArcmin.Value) ? "" : $"   ·   error {s.ErrorArcmin.Value:0.0}' (try {s.Iteration.Value})") + $"   ·   {s.Centerings.Value} centred";
            OffsetText = s.OffsetKnown.Value
                ? $"primary is {s.OffsetEastArcmin.Value:0.0}' east, {s.OffsetNorthArcmin.Value:0.0}' north of the guide scope (learned on pier {s.OffsetPierSide.Text})"
                : "no offset learned yet";
            if (!_loaded && s.Config.MountId.Text != "")
            {
                _loaded = true;
                var c = s.Config;
                Enabled = c.Enabled.Value; MountId = c.MountId.Text; GuideShooter = c.GuideShooterId.Text; PrimaryShooter = c.PrimaryShooterId.Text;
                ExposureSeconds = c.ExposureSeconds.Value; ToleranceArcmin = c.ToleranceArcmin.Value; MaxIterations = c.MaxIterations.Value;
                UseOffset = c.UseOffset.Value; SyncMount = c.SyncMount.Value;
            }
        });
        await _follower.StartAsync();
        _solvedHook = r => UiThread.Post(() =>
        {
            Solves.Insert(0, $"{DateTime.Now:HH:mm:ss}  {r.ShooterId.Text}: " + (r.Solved.Value
                ? $"{Sexagesimal.Format(r.RaHours.Value)} {Sexagesimal.Format(r.DecDegrees.Value, 0)}  PA {r.PositionAngle.Value:0.0}°  {r.PixelScale.Value:0.00}\"/px  ({r.Seconds.Value:0.0} s)"
                : "not solved: " + r.Message.Text));
            while (Solves.Count > 20) Solves.RemoveAt(Solves.Count - 1);
        });
        await _mesh.Node.HookEventAsync(SolveIds.Solved, _solvedHook, "centring: solve log");
    }

    private async Task Run(Task<CommandResult> call, string ok = "") { var r = await call; Message = r.Ok.Value ? ok : r.Error.Text; }

    [RelayCommand]
    private Task ApplyAsync() => Run(Commands.CallAsync(_mesh.Node, CenteringIds.Configure, new CenteringConfig
    {
        Enabled = Enabled, MountId = MountId ?? "", GuideShooterId = GuideShooter ?? "", PrimaryShooterId = PrimaryShooter ?? "",
        ExposureSeconds = ExposureSeconds, ToleranceArcmin = ToleranceArcmin, MaxIterations = MaxIterations, UseOffset = UseOffset, SyncMount = SyncMount,
    }), "saved");

    [RelayCommand] private Task CalibrateAsync() => Run(Commands.CallAsync(_mesh.Node, CenteringIds.CalibrateOffset, NOTESVoid.Void));
    [RelayCommand] private Task ClearOffsetAsync() => Run(Commands.CallAsync(_mesh.Node, CenteringIds.ClearOffset, NOTESVoid.Void));
    [RelayCommand] private Task CenterNowAsync() => Run(Commands.CallAsync(_mesh.Node, CenteringIds.CenterNow, new SkyTarget { RaHours = double.NaN, DecDegrees = double.NaN, Epoch = "J2000" }));

    [RelayCommand]
    private async Task SolveGuideAsync()
    {
        if (GuideShooter is null) { Message = "pick the guide camera"; return; }
        Message = "solving...";
        var r = await _mesh.Node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest { ShooterId = GuideShooter, ExposureSeconds = ExposureSeconds }, TimeSpan.FromSeconds(ExposureSeconds + 200));
        Message = r?.FirstOrDefault() is { } x ? (x.Solved.Value ? "" : x.Message.Text) : "no plate solver on the mesh";
    }

    public void Dispose()
    {
        _follower?.Dispose();
        if (_solvedHook is not null) { try { _mesh.Node.UnhookEvent(SolveIds.Solved, _solvedHook); } catch (ObjectDisposedException) { } }
    }
}
