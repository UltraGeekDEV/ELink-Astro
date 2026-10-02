using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
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

    private Action<GuideStep>? _stepHook;
    private readonly List<GuideStep> _steps = new();

    public ScopePanelViewModel(MeshSession mesh, string scopeId, string displayName, string guiderId = "")
    {
        _mesh = mesh; ScopeId = scopeId; DisplayName = displayName; GuiderId = guiderId;
    }

    // its own guiding (when the scope has a guider)
    public string GuiderId { get; }
    public bool HasGuider => GuiderId != "";
    [ObservableProperty] private string _guidePhase = "--";
    [ObservableProperty] private string _guideText = "";
    [ObservableProperty] private string _guideCalibration = "";
    [ObservableProperty] private IList<Point> _raGraph = new List<Point>();
    [ObservableProperty] private IList<Point> _decGraph = new List<Point>();
    public const double GraphWidth = 600, GraphHeight = 120, GraphArcsec = 4;

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

        if (HasGuider)
        {
            var guider = new Follower<GuiderState>(node, GuiderIds.State(GuiderId), GuiderIds.GetState(GuiderId), g =>
            {
                GuidePhase = g.Phase.Text + (g.Phase.Text == "Guiding" ? (g.Settled.Value ? ", settled" : ", settling") : "");
                GuideText = (double.IsNaN(g.RmsTotalArcsec.Value)
                                ? $"error {g.ErrorPixels.Value:0.00} px"
                                : $"RMS {g.RmsTotalArcsec.Value:0.00}\" (RA {g.RmsRaArcsec.Value:0.00}\", Dec {g.RmsDecArcsec.Value:0.00}\")") +
                            $"   ·   {g.Stars.Value} stars   ·   {g.Frames.Value} frames   ·   {g.Dithers.Value} dithers   ·   {g.Output.Text}" +
                            (g.Message.Text != "" ? $"   ·   {g.Message.Text}" : "");
                GuideCalibration = g.Calibration.Text;
            });
            _disposables.Add(guider);
            await guider.StartAsync();
            _stepHook = st => UiThread.Post(() =>
            {
                _steps.Add(st); if (_steps.Count > 100) _steps.RemoveAt(0);
                RaGraph = Graph(st2 => st2.RaArcsec.Value); DecGraph = Graph(st2 => st2.DecArcsec.Value);
            });
            await node.HookEventAsync(GuiderIds.Step(GuiderId), _stepHook, "scope guide graph");
        }
    }

    /// <summary>The last 100 guide errors as a line: ±GraphArcsec over the graph's height, newest on the right.</summary>
    private List<Point> Graph(Func<GuideStep, double> value)
    {
        var pts = new List<Point>();
        double dx = GraphWidth / 99;
        for (int i = 0; i < _steps.Count; i++)
        {
            double v = value(_steps[i]);
            if (double.IsNaN(v)) v = 0;
            double y = GraphHeight / 2 - Math.Clamp(v / GraphArcsec, -1, 1) * GraphHeight / 2;
            pts.Add(new Point(GraphWidth - (_steps.Count - 1 - i) * dx, y));
        }
        return pts;
    }

    [RelayCommand] private Task StartGuidingAsync() => Run(Commands.CallAsync(_mesh.Node, GuiderIds.Start(GuiderId), new GuideStartRequest()));
    [RelayCommand] private Task StopGuidingAsync() => Run(Commands.CallAsync(_mesh.Node, GuiderIds.Stop(GuiderId), NOTESVoid.Void, TimeSpan.FromSeconds(60)));
    [RelayCommand] private Task RecalibrateAsync() => Run(Commands.CallAsync(_mesh.Node, GuiderIds.Start(GuiderId), new GuideStartRequest { Recalibrate = true }, TimeSpan.FromSeconds(60)));
    [RelayCommand] private Task DitherNowAsync() => Run(Commands.CallAsync(_mesh.Node, GuiderIds.Dither(GuiderId), new DitherRequest { Pixels = 5, TimeoutSeconds = 90 }, TimeSpan.FromSeconds(120)));

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
        if (_stepHook is not null) { try { _mesh.Node.UnhookEvent(GuiderIds.Step(GuiderId), _stepHook); } catch (ObjectDisposedException) { } }
        foreach (var d in _disposables) d.Dispose();
    }
}
