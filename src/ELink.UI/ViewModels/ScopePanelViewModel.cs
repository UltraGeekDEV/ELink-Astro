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

    public ScopePanelViewModel(MeshSession mesh, string scopeId, string displayName, string guiderId = "", IReadOnlyList<string>? trainIds = null)
    {
        _mesh = mesh; ScopeId = scopeId; DisplayName = displayName; GuiderId = guiderId; TrainIds = trainIds ?? [];
    }

    // its trains' cooled cameras
    public IReadOnlyList<string> TrainIds { get; }
    private readonly Dictionary<string, string> _coolerLines = new();
    [ObservableProperty] private string _coolerText = "";
    [ObservableProperty] private bool _hasCoolers;

    private void ShowCoolers(TrainState t)
    {
        static string T(double v) => double.IsNaN(v) ? "--" : v.ToString("0.0", CultureInfo.InvariantCulture);
        var lines = t.Cameras.Where(c => c.Cooler.Text != "").Select(c =>
            $"{c.CameraId.Text}: {c.Cooler.Text}  {T(c.Temperature.Value)} °C" + (double.IsNaN(c.CoolerSetPoint.Value) ? "" : $"  (set point {T(c.CoolerSetPoint.Value)} °C)")).ToList();
        lock (_coolerLines) { _coolerLines[t.Id.Text] = string.Join("\n", lines); CoolerText = string.Join("\n", _coolerLines.Values.Where(v => v != "")); HasCoolers = CoolerText != ""; }
    }

    [RelayCommand] private Task CoolAsync() => CoolerCommandAsync(true);
    [RelayCommand] private Task WarmAsync() => CoolerCommandAsync(false);
    private async Task CoolerCommandAsync(bool cool)
    {
        foreach (var t in TrainIds)
        {
            var r = await Commands.CallAsync(_mesh.Node, cool ? ELink.Contracts.Composition.TrainIds.Cool(t) : ELink.Contracts.Composition.TrainIds.Warm(t), Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid.Void);
            if (!r.Ok.Value && !r.Error.Text.StartsWith("nobody provides")) Message = r.Error.Text;
        }
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
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ObserveCommand), nameof(GotoCommand), nameof(ExposeCommand), nameof(AbortCommand))] private bool _observing;
    private bool CanStartWork() => !Observing;
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
    public string Progress => Observing ? $"{ShotsDone} of {ShotsPlanned}" : "";
    /// <summary>0..1 of the running observation, for the progress bar.</summary>
    public double ProgressFraction => Observing && ShotsPlanned > 0 ? Math.Clamp((double)ShotsDone / ShotsPlanned, 0, 1) : 0;

    /// <summary>The scope's state in plain words, and the style class of its chip: idle | busy | ok | warn | error.</summary>
    public string ChipText => ScopePhase switch
    {
        "Disconnected" => "Not connected", "Idle" => "Idle", "Pointing" => "Slewing", "OnTarget" => "On target", "Centering" => "Centring",
        "Focusing" => "Focusing", "Cooling" => "Cooling down", "WaitingForFlip" => "Waiting to flip", "Flipping" => "Flipping", "Guiding" => "Guiding",
        "Dithering" => "Dithering", "Exposing" => Observing && ShotsPlanned > 0 ? $"Exposing {ShotsDone + 1} of {ShotsPlanned}" : "Exposing", "Error" => "Error", var other => other,
    };
    public string ChipClass => ScopePhase switch
    {
        "Idle" => "idle", "OnTarget" => "ok", "Error" => "error", "Disconnected" => "warn", _ => "busy",
    };
    /// <summary>One line on where the pointing stands.</summary>
    public string PointerLine => PointerPhase switch
    {
        "--" or "Disconnected" => "mount not connected", "Slewing" => "slewing…", "Tracking" when OnTarget => "on target, tracking", "Tracking" => "tracking",
        "Parked" => "parked", var other => OnTarget ? $"{other}, on target" : other,
    };
    private void ScopeChanged()
    {
        foreach (var n in new[] { nameof(Progress), nameof(ProgressFraction), nameof(ChipText), nameof(ChipClass), nameof(PointerLine) }) OnPropertyChanged(n);
    }
    partial void OnObservingChanged(bool value) => ScopeChanged();
    partial void OnShotsDoneChanged(int value) => ScopeChanged();
    partial void OnShotsPlannedChanged(int value) => ScopeChanged();
    partial void OnScopePhaseChanged(string value) => ScopeChanged();
    partial void OnPointerPhaseChanged(string value) => ScopeChanged();
    partial void OnOnTargetChanged(bool value) => ScopeChanged();

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

        foreach (var t in TrainIds)
        {
            var train = new Follower<TrainState>(node, ELink.Contracts.Composition.TrainIds.State(t), ELink.Contracts.Composition.TrainIds.GetState(t), ShowCoolers);
            _disposables.Add(train);
            await train.StartAsync();
        }

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

    [RelayCommand(CanExecute = nameof(CanStartWork))] private async Task ObserveAsync()
    {
        if (!TryTarget(out var t)) return;
        await Run(Commands.CallAsync(_mesh.Node, ScopeIds.Command(ScopeId, "Observe"),
            new ObserveRequest { Target = t, Exposure = Exposure(), Count = Math.Max(1, Count) }));
    }
    [RelayCommand(CanExecute = nameof(CanStartWork))] private async Task GotoAsync() { if (TryTarget(out var t)) await Run(Commands.CallAsync(_mesh.Node, PointerIds.Goto(ScopeId), t)); }
    [RelayCommand(CanExecute = nameof(CanStartWork))] private Task ExposeAsync() => Run(Commands.CallAsync(_mesh.Node, ShooterIds.Expose(ScopeId), Exposure()));
    [RelayCommand(CanExecute = nameof(Observing))] private Task AbortAsync() => Run(Commands.CallAsync(_mesh.Node, ScopeIds.Command(ScopeId, "Abort"), NOTESVoid.Void));

    public void Dispose()
    {
        if (_shotHook is not null) { try { _mesh.Node.UnhookEvent(ShooterIds.Shot(ScopeId), _shotHook); } catch (ObjectDisposedException) { } }
        if (_stepHook is not null) { try { _mesh.Node.UnhookEvent(GuiderIds.Step(GuiderId), _stepHook); } catch (ObjectDisposedException) { } }
        foreach (var d in _disposables) d.Dispose();
    }
}
