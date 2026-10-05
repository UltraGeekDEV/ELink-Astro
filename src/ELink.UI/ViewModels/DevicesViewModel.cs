namespace ELink.UI.ViewModels;

/// <summary>The drivers and the devices they bring, on one page: start the drivers, then connect what appears.</summary>
public sealed class DevicesViewModel(ProfilesViewModel drivers, EquipmentViewModel equipment)
{
    public ProfilesViewModel Drivers { get; } = drivers;
    public EquipmentViewModel Equipment { get; } = equipment;
}
