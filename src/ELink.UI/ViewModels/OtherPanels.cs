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
