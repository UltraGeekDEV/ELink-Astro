using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>One image in the schedule, with its conditions and what the scheduler thinks of it now.</summary>
public sealed partial class ScheduleRow : ObservableObject
{
    public ScheduleRow(ScheduleEntry entry)
    {
        Entry = entry;
        _priority = entry.Priority.Value; _enabled = entry.Enabled.Value; _requireDark = entry.RequireDark.Value;
        _minAltitude = double.IsNaN(entry.MinAltitudeDegrees.Value) ? 0 : entry.MinAltitudeDegrees.Value;
        _minMoonSeparation = entry.MinMoonSeparationDegrees.Value; _maxMoonPercent = entry.MaxMoonIllumination.Value * 100;
    }

    public ScheduleEntry Entry { get; }
    public string Label => Entry.Request.Label.Text;
    public string What => $"{Entry.Request.ScopeIds.Count} scope(s) · {(Entry.Request.TargetSeconds.Value > 0 ? $"{Entry.Request.TargetSeconds.Value / 60:0} min per spot" : "until stopped")}";
    [ObservableProperty] private int _priority;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _requireDark;
    [ObservableProperty] private double _minAltitude;          // 0 = the site's horizon
    [ObservableProperty] private double _minMoonSeparation;
    [ObservableProperty] private double _maxMoonPercent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusClass))] private string _status = "";
    /// <summary>The scheduler's word for this entry (Running, Possible, Waiting, Complete, Disabled).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusClass))] private string _verdict = "";
    public string StatusClass => Verdict switch { "Running" => "busy", "Complete" => "ok", "Possible" => "ok", "Waiting" => "warn", _ => "idle" };

    public ScheduleEntry ToEntry()
    {
        Span<byte> bytes = Entry.ToBytes();
        var e = new ScheduleEntry(); e.FromBytes(ref bytes);
        e.Priority = Priority; e.Enabled = Enabled; e.RequireDark = RequireDark; e.MinAltitudeDegrees = MinAltitude > 0 ? MinAltitude : double.NaN;
        e.MinMoonSeparationDegrees = MinMoonSeparation; e.MaxMoonIllumination = Math.Clamp(MaxMoonPercent / 100, 0, 1);
        return e;
    }
}

/// <summary>The schedule: images to take, best first while they are possible, night after night.</summary>
public sealed partial class ScheduleViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private Follower<SchedulerState>? _follower;
    private SchedulerState? _lastState;

    public ScheduleViewModel(MeshSession mesh)
    {
        _mesh = mesh;
        Rows.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasRows)); RunCommand.NotifyCanExecuteChanged(); };
    }

    public ObservableCollection<ScheduleRow> Rows { get; } = new();
    [ObservableProperty] private ScheduleRow? _selectedRow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRunning), nameof(PhaseText), nameof(PhaseClass))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private string _phase = "Stopped";
    public string PhaseText => Phase switch { "Imaging" => "Running", "Waiting" => "Waiting for something to become possible", "AllDone" => "Everything is done", _ => "Stopped" };
    public string PhaseClass => Phase switch { "Imaging" => "busy", "Waiting" => "warn", "AllDone" => "ok", _ => "idle" };
    [ObservableProperty] private string _running = "";
    [ObservableProperty] private string _stateMessage = "";
    [ObservableProperty] private string _message = "";

    public async Task StartAsync()
    {
        _follower = new Follower<SchedulerState>(_mesh.Node, SchedulerIds.State, SchedulerIds.GetState, s =>
        {
            Phase = s.Phase.Text; Running = s.Running.Text; StateMessage = s.Message.Text;
            _lastState = s;
            if (!Rows.Select(r => r.Label).SequenceEqual(s.Entries.Select(e => e.Label.Text))) _ = ReloadAsync();
            ApplyStatuses();
        });
        await _follower.StartAsync();
        await ReloadAsync();
    }

    private void ApplyStatuses()
    {
        if (_lastState is not { } s) return;
        foreach (var e in s.Entries)
            if (Rows.FirstOrDefault(r => r.Label == e.Label.Text) is { } row)
            {
                row.Verdict = e.Status.Text;
                string word = e.Status.Text switch { "Running" => "Imaging now", "Possible" => "Can run now", "Waiting" => "Waiting", "Complete" => "Done", "Disabled" => "Off", _ => e.Status.Text };
                row.Status = word + (e.Reason.Text != "" && e.Status.Text is "Waiting" ? $": {e.Reason.Text}" : "") + (e.CompletePercent.Value > 0 && e.Status.Text != "Complete" ? $" · {e.CompletePercent.Value:0}% deep" : "");
            }
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, Schedule>(SchedulerIds.GetSchedule, NOTESVoid.Void, TimeSpan.FromSeconds(10));
        var s = answers?.FirstOrDefault();
        if (s is null) return;
        UiThread.Post(() =>
        {
            _loading = true;
            Rows.Clear();
            foreach (var e in s.Entries) { var row = new ScheduleRow(e); row.PropertyChanged += OnRowEdited; Rows.Add(row); }
            _loading = false;
            ApplyStatuses();
        });
    }

    private bool _loading;
    private CancellationTokenSource? _applyCts;
    private static readonly HashSet<string> Editable = new()
    {
        nameof(ScheduleRow.Priority), nameof(ScheduleRow.Enabled), nameof(ScheduleRow.RequireDark), nameof(ScheduleRow.MinAltitude), nameof(ScheduleRow.MinMoonSeparation), nameof(ScheduleRow.MaxMoonPercent),
    };

    /// <summary>A condition was changed: apply it a moment later (no separate button to forget).</summary>
    private void OnRowEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName is null || !Editable.Contains(e.PropertyName)) return;
        _applyCts?.Cancel();
        var cts = _applyCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(700, cts.Token); UiThread.Post(() => _ = ApplyAsync()); }
            catch (OperationCanceledException) { }
        });
    }

    private async Task SaveAsync(IEnumerable<ScheduleEntry> entries)
    {
        var s = new Schedule();
        foreach (var e in entries) s.Entries.Add(e);
        var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.SetSchedule, s, TimeSpan.FromSeconds(30));
        Message = r.Ok.Value ? "" : r.Error.Text;
        if (!r.Ok.Value) _mesh.Notices.Error(r.Error.Text, "Queue");
        await ReloadAsync();
    }

    [RelayCommand] private Task ApplyAsync() => SaveAsync(Rows.Select(r => r.ToEntry()));

    [RelayCommand]
    private Task RemoveAsync() => SelectedRow is { } sel ? SaveAsync(Rows.Where(r => r != sel).Select(r => r.ToEntry())) : Task.CompletedTask;

    /// <summary>Takes one image out of the queue.</summary>
    [RelayCommand]
    private Task RemoveRowAsync(ScheduleRow? row) => row is null ? Task.CompletedTask : SaveAsync(Rows.Where(r => r != row).Select(r => r.ToEntry()));

    public bool HasRows => Rows.Count > 0;
    public bool IsRunning => Phase is "Imaging" or "Waiting";

    /// <summary>Adds (or replaces, by name) an image request from the Sky view.</summary>
    public Task AddAsync(ImagingRequest request) =>
        SaveAsync(Rows.Where(r => !string.Equals(r.Label, request.Label.Text, StringComparison.OrdinalIgnoreCase)).Select(r => r.ToEntry())
            .Append(new ScheduleEntry { Request = request }));

    [RelayCommand(CanExecute = nameof(CanRun))] private async Task RunAsync() { var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.Start, NOTESVoid.Void); Message = r.Ok.Value ? "" : r.Error.Text; }
    private bool CanRun() => !IsRunning && Rows.Count > 0;
    [RelayCommand(CanExecute = nameof(IsRunning))] private async Task StopAsync() { var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.Stop, NOTESVoid.Void, TimeSpan.FromSeconds(60)); Message = r.Ok.Value ? "" : r.Error.Text; }

    public void Dispose() => _follower?.Dispose();
}
