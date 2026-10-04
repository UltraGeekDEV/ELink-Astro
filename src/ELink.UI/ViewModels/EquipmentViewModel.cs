using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.UI.Infrastructure;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>What is plugged in: every device with its status, connect and disconnect, and a panel for each.</summary>
public sealed partial class EquipmentViewModel : ObservableObject
{
    private readonly MeshSession _mesh;
    private readonly Func<DeviceItem, Task> _open;

    public EquipmentViewModel(MeshSession mesh, CatalogViewModel catalog, Func<DeviceItem, Task> open)
    {
        _mesh = mesh; Catalog = catalog; _open = open;
        catalog.Equipment.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasNone)); OnPropertyChanged(nameof(HasSome)); };
    }

    public CatalogViewModel Catalog { get; }
    public bool HasNone => Catalog.Equipment.Count == 0;
    public bool HasSome => !HasNone;
    [ObservableProperty] private string _message = "";

    [RelayCommand] private Task OpenAsync(DeviceItem? item) => item is null ? Task.CompletedTask : _open(item);
    [RelayCommand] private Task RefreshAsync() => Catalog.RefreshAsync();

    [RelayCommand]
    private async Task ToggleAsync(DeviceItem? item)
    {
        if (item is null) return;
        var r = await Commands.CallAsync(_mesh.Node, EquipmentIds.Command(item.Kind, item.Id, "Connect"), (BinaryConvertibleBool)!item.Connected);
        if (!r.Ok.Value) Message = $"{item.DisplayName}: {r.Error.Text}";
    }

    [RelayCommand]
    private async Task ConnectAllAsync()
    {
        Message = "";
        var failures = new List<string>();
        foreach (var d in Catalog.Equipment.Where(d => !d.Connected).ToList())
        {
            var r = await Commands.CallAsync(_mesh.Node, EquipmentIds.Command(d.Kind, d.Id, "Connect"), (BinaryConvertibleBool)true);
            if (!r.Ok.Value) failures.Add($"{d.DisplayName}: {r.Error.Text}");
        }
        Message = string.Join("; ", failures);
    }
}
