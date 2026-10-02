using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed record MasterRow(string Id, string Line);

/// <summary>The calibration library: take darks, biases and flats with one camera; the live stack uses the masters that suit its frames.</summary>
public sealed partial class CalibrationViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private Follower<CalibrationState>? _follower;

    public CalibrationViewModel(MeshSession mesh, CatalogViewModel catalog)
    {
        _mesh = mesh; _catalog = catalog;
        catalog.CompositionChanged += () => UiThread.Post(RebuildChoices);
        RebuildChoices();
    }

    public ObservableCollection<string> Cameras { get; } = new();
    public string[] Kinds { get; } = ["Dark", "Bias", "Flat"];
    public ObservableCollection<MasterRow> Masters { get; } = new();
    [ObservableProperty] private string? _selectedCamera;
    [ObservableProperty] private string _kind = "Dark";
    [ObservableProperty] private double _exposureSeconds = 60;
    [ObservableProperty] private int _count = 20;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _gain = "";
    [ObservableProperty] private string _iso = "";
    [ObservableProperty] private int _flatLevelPercent = 50;
    [ObservableProperty] private MasterRow? _selectedMaster;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _message = "";
    public string Hint => Kind switch
    {
        "Dark" => "Cap the scope. Same exposure, gain and sensor temperature as the lights they are for.",
        "Bias" => "Cap the scope. The shortest exposure the camera takes; stands in for darks that are missing.",
        _ => "Even light over the aperture (a panel or the twilight sky). Exposure 0 finds the one that fills the camera to the level below.",
    };
    partial void OnKindChanged(string value) { OnPropertyChanged(nameof(Hint)); if (value is "Flat" or "Bias") ExposureSeconds = 0; }

    /// <summary>Each camera on its own: imaging cameras of trains, then single-camera shooters.</summary>
    private void RebuildChoices()
    {
        var c = _catalog.Composition;
        var list = c.Trains.SelectMany(t => t.Cameras.Where(x => x.Role.Text != "Guiding").Select(x => TrainIds.CameraShooter(t.Id.Text, x.CameraId.Text)))
            .Concat(c.CameraShooters.Select(s => s.Id.Text)).Distinct().ToList();
        if (Cameras.SequenceEqual(list)) return;
        Cameras.Clear(); foreach (var x in list) Cameras.Add(x);
        SelectedCamera ??= Cameras.FirstOrDefault();
    }

    public async Task StartAsync()
    {
        _follower = new Follower<CalibrationState>(_mesh.Node, CalibrationIds.State, CalibrationIds.GetState, s =>
        {
            Status = s.Phase.Text switch
            {
                "Capturing" => $"capturing {s.Done.Value}/{s.Count.Value} at {s.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)} s",
                "FindingExposure" => "finding the flat exposure",
                _ => s.Phase.Text,
            } + (s.Message.Text != "" ? " — " + s.Message.Text : "");
            if (s.Phase.Text == "Done") _ = ReloadAsync();
        });
        await _follower.StartAsync();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var answers = await _mesh.Node.CallFunctionAsync<NOTESVoid, CalibrationMasters>(CalibrationIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(10));
        var rows = (answers?.FirstOrDefault()?.Masters ?? new()).OrderBy(m => m.ShooterId.Text).ThenBy(m => m.Kind.Text).ThenBy(m => m.ExposureSeconds.Value)
            .Select(m => new MasterRow(m.Id.Text, string.Join("  ·  ", new[]
            {
                m.ShooterId.Text, m.Kind.Text, m.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " s",
                m.Filter.Text != "" ? m.Filter.Text : null,
                double.IsNaN(m.Gain.Value) ? null : $"gain {m.Gain.Value:0}", m.Iso.Text != "" ? $"ISO {m.Iso.Text}" : null,
                double.IsNaN(m.TemperatureC.Value) ? null : $"{m.TemperatureC.Value:0.0} °C", m.BinX.Value > 1 ? $"bin {m.BinX.Value}" : null,
                $"{m.Width.Value}×{m.Height.Value}", $"{m.Frames.Value} frames", m.CreatedUtc.Text.Length >= 10 ? m.CreatedUtc.Text[..10] : null,
            }.Where(x => x is not null)))).ToList();
        UiThread.Post(() => { Masters.Clear(); foreach (var r in rows) Masters.Add(r); });
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (SelectedCamera is null) { Message = "choose a camera"; return; }
        var r = new CaptureRequest
        {
            ShooterId = SelectedCamera, Kind = Kind, ExposureSeconds = ExposureSeconds, Count = Count, Filter = Filter.Trim(), Iso = Iso.Trim(),
            Gain = double.TryParse(Gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : double.NaN, FlatTargetFraction = FlatLevelPercent / 100.0,
        };
        var res = await Commands.CallAsync(_mesh.Node, CalibrationIds.Capture, r);
        Message = res.Ok.Value ? "" : res.Error.Text;
    }

    [RelayCommand] private async Task AbortAsync() => await Commands.CallAsync(_mesh.Node, CalibrationIds.Abort, NOTESVoid.Void);

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedMaster is not { } m) return;
        var res = await Commands.CallAsync(_mesh.Node, CalibrationIds.Delete, (BinaryConvertibleString)m.Id);
        Message = res.Ok.Value ? "" : res.Error.Text;
        await ReloadAsync();
    }

    public void Dispose() => _follower?.Dispose();
}
