using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed record DriverItem(string Group, string Device, string Executable)
{
    public string Line => $"{Device}   ({Group}, {Executable})";
}

/// <summary>Equipment profiles: which INDI drivers make up a rig. ELink runs them itself and connects the devices.</summary>
public sealed partial class ProfilesViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private Follower<ProfileState>? _follower;
    private List<DriverItem> _catalog = new();

    public ProfilesViewModel(MeshSession mesh)
    {
        _mesh = mesh;
        Chosen.CollectionChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<DriverItem> Matches { get; } = new();
    public ObservableCollection<string> Chosen { get; } = new();
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StopProfileCommand))] private bool _running;
    private bool CanSave() => Label.Trim() != "" && Chosen.Count > 0;
    private bool CanUseLabel() => Label.Trim() != "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartProfileCommand), nameof(DeleteCommand))] private string? _selectedProfile;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(StartProfileCommand), nameof(DeleteCommand))] private string _label = "";
    [ObservableProperty] private int _port = 7624;
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DriverItem? _selectedMatch;
    [ObservableProperty] private string? _selectedChosen;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _message = "";
    private List<EquipmentProfile> _profiles = new();

    public async Task StartAsync()
    {
        _follower = new Follower<ProfileState>(_mesh.Node, ProfileIds.State, ProfileIds.GetState, s =>
        {
            Running = s.Running.Text != "";
            Status = s.Running.Text == "" ? $"no profile running{(s.Message.Text != "" ? " — " + s.Message.Text : "")}"
                : $"{s.Running.Text}: {s.Phase.Text} on port {s.Port.Value} · {string.Join(", ", s.Drivers.Select(d => d.Text))}" + (s.Message.Text != "" ? $" — {s.Message.Text}" : "");
        });
        await _follower.StartAsync();
        var cat = await _mesh.Node.CallFunctionAsync<NOTESVoid, DriverCatalog>(ProfileIds.Catalog, NOTESVoid.Void, TimeSpan.FromSeconds(20));
        _catalog = (cat?.FirstOrDefault()?.Drivers ?? new()).Select(d => new DriverItem(d.Group.Text, d.Device.Text, d.Executable.Text)).ToList();
        await ReloadAsync();
        UiThread.Post(Filter);
    }

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _catalog.Where(d => words.All(w => d.Line.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(200).ToList();
        Matches.Clear(); foreach (var h in hits) Matches.Add(h);
    }

    private async Task ReloadAsync()
    {
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, EquipmentProfiles>(ProfileIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(10));
        _profiles = (answers?.FirstOrDefault()?.Profiles ?? new()).ToList();
        UiThread.Post(() => { Profiles.Clear(); foreach (var p in _profiles) Profiles.Add(p.Label.Text); });
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_profiles.FirstOrDefault(p => p.Label.Text == value) is not { } p) return;
        Label = p.Label.Text; Port = p.Port.Value; AutoConnect = p.AutoConnect.Value;
        Chosen.Clear(); foreach (var d in p.Drivers) Chosen.Add(d.Text);
    }

    [RelayCommand] private void AddDriver() { if (SelectedMatch is { } m && !Chosen.Contains(m.Executable)) Chosen.Add(m.Executable); }
    [RelayCommand] private void RemoveDriver() { if (SelectedChosen is { } c) Chosen.Remove(c); }

    private async Task Run(Task<CommandResult> call, string ok) { var r = await call; Message = r.Ok.Value ? ok : r.Error.Text; await ReloadAsync(); }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        var p = new EquipmentProfile { Label = Label.Trim(), Port = Port, AutoConnect = AutoConnect };
        foreach (var d in Chosen) p.Drivers.Add(d);
        return Run(Commands.CallAsync(_mesh.Node, ProfileIds.Save, p), $"profile '{Label}' saved");
    }

    [RelayCommand(CanExecute = nameof(CanUseLabel))] private Task DeleteAsync() => Run(Commands.CallAsync(_mesh.Node, ProfileIds.Delete, (BinaryConvertibleString)Label.Trim()), "deleted");
    [RelayCommand(CanExecute = nameof(CanUseLabel))] private Task StartProfileAsync() => Run(Commands.CallAsync(_mesh.Node, ProfileIds.Start, (BinaryConvertibleString)Label.Trim(), TimeSpan.FromSeconds(60)), "");
    [RelayCommand(CanExecute = nameof(Running))] private Task StopProfileAsync() => Run(Commands.CallAsync(_mesh.Node, ProfileIds.Stop, NOTESVoid.Void, TimeSpan.FromSeconds(60)), "");

    public void Dispose() => _follower?.Dispose();
}
