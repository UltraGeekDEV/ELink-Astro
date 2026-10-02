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
    [ObservableProperty] private string _status = "";

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

    public ScheduleViewModel(MeshSession mesh) { _mesh = mesh; }

    public ObservableCollection<ScheduleRow> Rows { get; } = new();
    [ObservableProperty] private ScheduleRow? _selectedRow;
    [ObservableProperty] private string _phase = "Stopped";
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
                row.Status = $"{e.Status.Text}" + (e.Reason.Text != "" ? $" — {e.Reason.Text}" : "") + (e.CompletePercent.Value > 0 ? $" · {e.CompletePercent.Value:0}% deep" : "");
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, Schedule>(SchedulerIds.GetSchedule, NOTESVoid.Void, TimeSpan.FromSeconds(10));
        var s = answers?.FirstOrDefault();
        if (s is null) return;
        UiThread.Post(() => { Rows.Clear(); foreach (var e in s.Entries) Rows.Add(new ScheduleRow(e)); ApplyStatuses(); });
    }

    private async Task SaveAsync(IEnumerable<ScheduleEntry> entries)
    {
        var s = new Schedule();
        foreach (var e in entries) s.Entries.Add(e);
        var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.SetSchedule, s, TimeSpan.FromSeconds(30));
        Message = r.Ok.Value ? "" : r.Error.Text;
        await ReloadAsync();
    }

    [RelayCommand] private Task ApplyAsync() => SaveAsync(Rows.Select(r => r.ToEntry()));

    [RelayCommand]
    private Task RemoveAsync() => SelectedRow is { } sel ? SaveAsync(Rows.Where(r => r != sel).Select(r => r.ToEntry())) : Task.CompletedTask;

    /// <summary>Adds (or replaces, by name) an image request from the Image tab.</summary>
    public Task AddAsync(ImagingRequest request) =>
        SaveAsync(Rows.Where(r => !string.Equals(r.Label, request.Label.Text, StringComparison.OrdinalIgnoreCase)).Select(r => r.ToEntry())
            .Append(new ScheduleEntry { Request = request }));

    [RelayCommand] private async Task RunAsync() { var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.Start, NOTESVoid.Void); Message = r.Ok.Value ? "" : r.Error.Text; }
    [RelayCommand] private async Task StopAsync() { var r = await Commands.CallAsync(_mesh.Node, SchedulerIds.Stop, NOTESVoid.Void, TimeSpan.FromSeconds(60)); Message = r.Ok.Value ? "" : r.Error.Text; }

    public void Dispose() => _follower?.Dispose();
}
