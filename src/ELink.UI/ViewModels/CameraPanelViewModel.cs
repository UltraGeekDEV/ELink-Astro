using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public partial class CameraPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Camera, id, displayName)
{
    private Action<FrameEvent>? _frameHook;

    [ObservableProperty] private string _phase = "Disconnected";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _temperatureText = "--";
    [ObservableProperty] private string _sensorText = "";
    [ObservableProperty] private string _binText = "";
    [ObservableProperty] private string _gainText = "";

    [ObservableProperty] private double _exposureSeconds = 1;
    [ObservableProperty] private string _frameType = "Light";
    [ObservableProperty] private int _binning = 1;
    [ObservableProperty] private double _targetTemperature = -10;
    public string[] FrameTypes { get; } = { "Light", "Dark", "Bias", "Flat" };
    public int[] Binnings { get; } = { 1, 2, 3, 4 };
    public FrameDisplay Preview { get; } = new();

    public override async Task StartAsync()
    {
        var f = Follow<CameraState>(s =>
        {
            Connected = s.Connected.Value; Phase = s.Phase.Text;
            RemainingText = s.Phase.Text == "Exposing" ? $"{s.ExposureRemaining.Value:0.0} s left" : "";
            TemperatureText = double.IsNaN(s.Temperature.Value) ? "--" : $"{s.Temperature.Value:0.0} °C";
            SensorText = s.SensorWidth.Value > 0 ? $"{s.SensorWidth.Value}×{s.SensorHeight.Value}, {s.PixelSizeUm.Value:0.##} µm, {s.BitsPerPixel.Value} bit" : "";
            BinText = Connected ? $"{s.BinX.Value}×{s.BinY.Value}" : "";
            GainText = double.IsNaN(s.Gain.Value) ? "--" : s.Gain.Value.ToString("0.#", CultureInfo.InvariantCulture);
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();

        _frameHook = frame =>
            Preview.Show(frame.Data.Data, frame.Format.Text, $"{frame.FrameType.Text} {frame.ExposureSeconds.Value:0.###} s");
        await Mesh.Node.HookEventAsync(EquipmentIds.Command(Kind, Id, "Frame"), _frameHook, "camera preview");
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private Task ExposeAsync() => Call("Expose", new ExposeRequest { Seconds = ExposureSeconds, FrameType = FrameType, BinX = Binning, BinY = Binning });
    [RelayCommand] private Task AbortAsync() => Call("AbortExposure");
    [RelayCommand] private Task CoolAsync() => Call("SetTemperature", (BinaryConvertibleDouble)TargetTemperature);

    public override void Dispose()
    {
        if (_frameHook is not null) { try { Mesh.Node.UnhookEvent(EquipmentIds.Command(Kind, Id, "Frame"), _frameHook); } catch (ObjectDisposedException) { } }
        base.Dispose();
    }
}
