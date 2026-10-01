using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public partial class FocuserPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Focuser, id, displayName)
{
    [ObservableProperty] private int _position;
    [ObservableProperty] private int _maxPosition;
    [ObservableProperty] private bool _moving;
    [ObservableProperty] private string _temperatureText = "--";
    [ObservableProperty] private int _targetPosition = 50000;
    [ObservableProperty] private int _stepSize = 100;

    public override async Task StartAsync()
    {
        var f = Follow<FocuserState>(s =>
        {
            Connected = s.Connected.Value; Position = s.Position.Value; MaxPosition = s.MaxPosition.Value; Moving = s.Moving.Value;
            TemperatureText = double.IsNaN(s.Temperature.Value) ? "--" : $"{s.Temperature.Value:0.0} °C";
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task MoveToAsync() => Call("MoveTo", (BinaryConvertibleInt32)TargetPosition);
    [RelayCommand] private Task InAsync() => Call("MoveBy", (BinaryConvertibleInt32)(-StepSize));
    [RelayCommand] private Task OutAsync() => Call("MoveBy", (BinaryConvertibleInt32)StepSize);
    [RelayCommand] private Task AbortAsync() => Call("Abort");
}

public partial class FilterWheelPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.FilterWheel, id, displayName)
{
    [ObservableProperty] private int _slot;
    [ObservableProperty] private bool _moving;
    [ObservableProperty] private string? _selectedFilter;
    public ObservableCollection<string> Filters { get; } = new();

    public override async Task StartAsync()
    {
        var f = Follow<FilterWheelState>(s =>
        {
            Connected = s.Connected.Value; Slot = s.Slot.Value; Moving = s.Moving.Value;
            var names = s.FilterNames.Select(n => n.Text).ToList();
            if (!names.SequenceEqual(Filters)) { Filters.Clear(); foreach (var n in names) Filters.Add(n); }
            SelectedFilter = Slot >= 1 && Slot <= names.Count ? names[Slot - 1] : null;
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task SelectAsync(string? filter) => filter is null ? Task.CompletedTask : Call("SelectFilter", (BinaryConvertibleString)filter);
}

public partial class RotatorPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Rotator, id, displayName)
{
    [ObservableProperty] private double _angle;
    [ObservableProperty] private bool _moving;
    [ObservableProperty] private double _targetAngle;

    public override async Task StartAsync()
    {
        var f = Follow<RotatorState>(s =>
        {
            Connected = s.Connected.Value; Angle = s.AngleDegrees.Value; Moving = s.Moving.Value;
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task MoveToAsync() => Call("MoveTo", (BinaryConvertibleDouble)TargetAngle);
    [RelayCommand] private Task SyncAsync() => Call("Sync", (BinaryConvertibleDouble)TargetAngle);
    [RelayCommand] private Task AbortAsync() => Call("Abort");
}

public partial class DomePanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Dome, id, displayName)
{
    [ObservableProperty] private double _azimuth;
    [ObservableProperty] private bool _moving;
    [ObservableProperty] private string _shutter = "Unknown";
    [ObservableProperty] private bool _parked;
    [ObservableProperty] private double _targetAzimuth;

    public override async Task StartAsync()
    {
        var f = Follow<DomeState>(s =>
        {
            Connected = s.Connected.Value; Azimuth = s.AzimuthDegrees.Value; Moving = s.Moving.Value; Shutter = s.Shutter.Text; Parked = s.Parked.Value;
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task GotoAsync() => Call("GotoAzimuth", (BinaryConvertibleDouble)TargetAzimuth);
    [RelayCommand] private Task OpenShutterAsync() => Call("Shutter", (BinaryConvertibleBool)true);
    [RelayCommand] private Task CloseShutterAsync() => Call("Shutter", (BinaryConvertibleBool)false);
    [RelayCommand] private Task ParkAsync() => Call("Park", (BinaryConvertibleBool)!Parked);
    [RelayCommand] private Task AbortAsync() => Call("Abort");
}

public sealed class WeatherRow
{
    public WeatherRow(string label, string value, string status) { Label = label; Value = value; Status = status; }
    public string Label { get; }
    public string Value { get; }
    public string Status { get; }
}

public partial class WeatherPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Weather, id, displayName)
{
    [ObservableProperty] private string _safety = "Unknown";
    [ObservableProperty] private bool _safe;
    public ObservableCollection<WeatherRow> Parameters { get; } = new();

    public override async Task StartAsync()
    {
        var f = Follow<WeatherState>(s =>
        {
            Connected = s.Connected.Value; Safety = s.Safety.Text; Safe = s.Safe.Value;
            Parameters.Clear();
            foreach (var p in s.Parameters)
                Parameters.Add(new WeatherRow(p.Label.Text != "" ? p.Label.Text : p.Id.Text, p.Value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), p.Status.Text));
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task RefreshAsync() => Call("Refresh");
}

public partial class GpsPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Gps, id, displayName)
{
    [ObservableProperty] private bool _hasFix;
    [ObservableProperty] private string _latitudeText = "--";
    [ObservableProperty] private string _longitudeText = "--";
    [ObservableProperty] private string _elevationText = "--";
    [ObservableProperty] private string _timeUtc = "";

    public override async Task StartAsync()
    {
        var f = Follow<GpsState>(s =>
        {
            Connected = s.Connected.Value; HasFix = s.HasFix.Value; TimeUtc = s.TimeUtc.Text;
            LatitudeText = double.IsNaN(s.LatitudeDegrees.Value) ? "--" : ELink.Core.Astro.Sexagesimal.Format(s.LatitudeDegrees.Value, 0);
            LongitudeText = double.IsNaN(s.LongitudeDegrees.Value) ? "--" : ELink.Core.Astro.Sexagesimal.Format(s.LongitudeDegrees.Value, 0);
            ElevationText = double.IsNaN(s.ElevationMeters.Value) ? "--" : $"{s.ElevationMeters.Value:0} m";
        });
        await f.StartAsync();
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task RefreshAsync() => Call("Refresh");
}
