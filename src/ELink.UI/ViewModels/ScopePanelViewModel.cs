using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>A smart scope: where it points, what it is doing, and the one-button "point and shoot" job.</summary>
public partial class ScopePanelViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly List<IDisposable> _disposables = new();
    private Action<ShotEvent>? _shotHook;

    public ScopePanelViewModel(MeshSession mesh, string scopeId, string displayName)
    {
        _mesh = mesh; ScopeId = scopeId; DisplayName = displayName;
    }

    public string ScopeId { get; }
    public string DisplayName { get; }
    public string Title => DisplayName;

    // what the scope is doing
    [ObservableProperty] private string _scopePhase = "Idle";
    [ObservableProperty] private string _scopeMessage = "";
    [ObservableProperty] private int _shotsDone;
    [ObservableProperty] private int _shotsPlanned;
    [ObservableProperty] private bool _observing;
    // where it points
    [ObservableProperty] private string _pointerPhase = "--";
    [ObservableProperty] private string _raText = "--";
    [ObservableProperty] private string _decText = "--";
    [ObservableProperty] private bool _onTarget;
    // what it shoots
    [ObservableProperty] private string _shooterPhase = "--";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _shotsPerRound = 1;

    // inputs
    [ObservableProperty] private string _targetRa = "05:35:17";
    [ObservableProperty] private string _targetDec = "-05:23:28";
    [ObservableProperty] private string _targetEpoch = "J2000";
    [ObservableProperty] private double _exposureSeconds = 5;
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _frameType = "Light";
    [ObservableProperty] private string _message = "";
    public string[] Epochs { get; } = { "J2000", "JNow" };
    public string[] FrameTypes { get; } = { "Light", "Dark", "Bias", "Flat" };

    public FrameDisplay Preview { get; } = new();
    public ObservableCollection<string> Shots { get; } = new();
    public string Progress => Observing ? $"{ShotsDone} / {ShotsPlanned} rounds" : "";
    partial void OnObservingChanged(bool value) => OnPropertyChanged(nameof(Progress));
    partial void OnShotsDoneChanged(int value) => OnPropertyChanged(nameof(Progress));
    partial void OnShotsPlannedChanged(int value) => OnPropertyChanged(nameof(Progress));

    public async Task StartAsync()
    {
        var node = _mesh.Node;
        var scope = new Follower<ScopeState>(node, ScopeIds.State(ScopeId), ScopeIds.GetState(ScopeId), s =>
        {
            ScopePhase = s.Phase.Text; ScopeMessage = s.Message.Text; ShotsDone = s.ShotsDone.Value; ShotsPlanned = s.ShotsPlanned.Value; Observing = s.Observing.Value;
        });
        var pointer = new Follower<PointerState>(node, PointerIds.State(ScopeId), PointerIds.GetState(ScopeId), s =>
        {
            PointerPhase = s.Phase.Text; OnTarget = s.OnTarget.Value;
            bool live = s.Phase.Text != "Disconnected";
            RaText = live ? Sexagesimal.Format(s.RaHours.Value) : "--"; DecText = live ? Sexagesimal.Format(s.DecDegrees.Value, 0) : "--";
        });
        var shooter = new Follower<ShooterState>(node, ShooterIds.State(ScopeId), ShooterIds.GetState(ScopeId), s =>
        {
            ShooterPhase = s.Phase.Text; FilterText = s.Filter.Text; ShotsPerRound = s.ShotsPerExposure.Value;
        });
        _disposables.AddRange(new IDisposable[] { scope, pointer, shooter });
        await scope.StartAsync(); await pointer.StartAsync(); await shooter.StartAsync();

        _shotHook = shot =>
        {
            string text = $"{DateTime.Now:HH:mm:ss}  {shot.Shooter.Text}  {shot.FrameType.Text} {shot.ExposureSeconds.Value:0.###}s" +
                          (shot.Filter.Text != "" ? $"  {shot.Filter.Text}" : "");
            UiThread.Post(() => { Shots.Insert(0, text); while (Shots.Count > 50) Shots.RemoveAt(Shots.Count - 1); });
            Preview.Show(shot.Data.Data, shot.Format.Text, text);
        };
        await node.HookEventAsync(ShooterIds.Shot(ScopeId), _shotHook, "scope preview");
    }

    private bool TryTarget(out SkyTarget target)
    {
        target = new SkyTarget();
        if (!Sexagesimal.TryParse(TargetRa, out var ra) || ra < 0 || ra >= 24) { Message = "RA must be 0..24 hours (e.g. 05:35:17)"; return false; }
        if (!Sexagesimal.TryParse(TargetDec, out var dec) || dec < -90 || dec > 90) { Message = "Dec must be -90..90 degrees"; return false; }
        target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = TargetEpoch };
        return true;
    }

    private ShooterExposure Exposure() => new() { Seconds = ExposureSeconds, FrameType = FrameType, Filter = Filter };

    private async Task Run(Task<CommandResult> call) { var r = await call; Message = r.Ok.Value ? "" : r.Error.Text; }

    [RelayCommand] private async Task ObserveAsync()
    {
        if (!TryTarget(out var t)) return;
        await Run(Commands.CallAsync(_mesh.Node, ScopeIds.Command(ScopeId, "Observe"),
            new ObserveRequest { Target = t, Exposure = Exposure(), Count = Math.Max(1, Count) }));
    }
    [RelayCommand] private async Task GotoAsync() { if (TryTarget(out var t)) await Run(Commands.CallAsync(_mesh.Node, PointerIds.Goto(ScopeId), t)); }
    [RelayCommand] private Task ExposeAsync() => Run(Commands.CallAsync(_mesh.Node, ShooterIds.Expose(ScopeId), Exposure()));
    [RelayCommand] private Task AbortAsync() => Run(Commands.CallAsync(_mesh.Node, ScopeIds.Command(ScopeId, "Abort"), NOTESVoid.Void));

    public void Dispose()
    {
        if (_shotHook is not null) { try { _mesh.Node.UnhookEvent(ShooterIds.Shot(ScopeId), _shotHook); } catch (ObjectDisposedException) { } }
        foreach (var d in _disposables) d.Dispose();
    }
}
