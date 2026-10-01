using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.Core.Astro;
using ELink.UI.Infrastructure;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public partial class MountPanelViewModel(MeshSession mesh, string id, string displayName)
    : DevicePanelViewModel(mesh, DeviceKinds.Mount, id, displayName)
{
    [ObservableProperty] private string _phase = "Disconnected";
    [ObservableProperty] private string _raText = "--";
    [ObservableProperty] private string _decText = "--";
    [ObservableProperty] private string _epoch = "";
    [ObservableProperty] private bool _tracking;
    [ObservableProperty] private bool _parked;
    [ObservableProperty] private string _pierSide = "";

    [ObservableProperty] private string _targetRa = "05:35:17";
    [ObservableProperty] private string _targetDec = "-05:23:28";
    [ObservableProperty] private string _targetEpoch = "J2000";
    public string[] Epochs { get; } = { "J2000", "JNow" };

    public string ParkButtonText => Parked ? "Unpark" : "Park";
    public string TrackButtonText => Tracking ? "Stop tracking" : "Track";
    partial void OnParkedChanged(bool value) => OnPropertyChanged(nameof(ParkButtonText));
    partial void OnTrackingChanged(bool value) => OnPropertyChanged(nameof(TrackButtonText));

    public override async Task StartAsync()
    {
        var f = Follow<MountState>(s =>
        {
            Connected = s.Connected.Value; Phase = s.Phase.Text; Epoch = s.Epoch.Text;
            RaText = Connected ? Sexagesimal.Format(s.RaHours.Value) : "--";
            DecText = Connected ? Sexagesimal.Format(s.DecDegrees.Value, 0) : "--";
            Tracking = s.Tracking.Value; Parked = s.Parked.Value; PierSide = s.PierSide.Text;
            if (s.Message.Text != "") Message = s.Message.Text;
        });
        await f.StartAsync();
    }

    private bool TryTarget(out SkyTarget target)
    {
        target = new SkyTarget();
        if (!Sexagesimal.TryParse(TargetRa, out var ra) || ra < 0 || ra >= 24) { Message = "RA must be 0..24 hours (e.g. 05:35:17)"; return false; }
        if (!Sexagesimal.TryParse(TargetDec, out var dec) || dec < -90 || dec > 90) { Message = "Dec must be -90..90 degrees"; return false; }
        target = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = TargetEpoch };
        return true;
    }

    [RelayCommand] private Task ConnectAsync() => ToggleConnectAsync();
    [RelayCommand] private async Task GotoAsync() { if (TryTarget(out var t)) await Call("Goto", t); }
    [RelayCommand] private async Task SyncAsync() { if (TryTarget(out var t)) await Call("Sync", t); }
    [RelayCommand] private Task AbortAsync() => Call("Abort");
    [RelayCommand] private Task ParkAsync() => Call("Park", (BinaryConvertibleBool)!Parked);
    [RelayCommand] private Task TrackAsync() => Call("SetTracking", (BinaryConvertibleBool)!Tracking);
}
