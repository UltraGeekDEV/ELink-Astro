using CommunityToolkit.Mvvm.ComponentModel;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>A live panel for one piece of equipment. It mirrors the device's state event and sends commands as
/// function calls; it knows the device only by kind and id.</summary>
public abstract partial class DevicePanelViewModel : ObservableObject, IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    protected MeshSession Mesh { get; }

    protected DevicePanelViewModel(MeshSession mesh, string kind, string id, string displayName)
    {
        Mesh = mesh; Kind = kind; Id = id; DisplayName = displayName;
    }

    public string Kind { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Title => $"{DisplayName}";

    [ObservableProperty] private bool _connected;
    /// <summary>Outcome of the last command, or the device's own message; empty when all is well.</summary>
    [ObservableProperty] private string _message = "";

    public string ConnectButtonText => Connected ? "Disconnect" : "Connect";
    /// <summary>Said above the panel while the device is not connected (its controls are greyed out).</summary>
    public string ConnectionHint => Connected ? "" : "Not connected: press Connect.";
    partial void OnConnectedChanged(bool value) { OnPropertyChanged(nameof(ConnectButtonText)); OnPropertyChanged(nameof(ConnectionHint)); }

    public abstract Task StartAsync();

    protected string StateId => EquipmentIds.State(Kind, Id);
    protected string GetStateId => EquipmentIds.GetState(Kind, Id);

    protected Follower<T> Follow<T>(Action<T> apply) where T : IBinaryConvertible, new()
    {
        var f = new Follower<T>(Mesh.Node, StateId, GetStateId, apply);
        _disposables.Add(f);
        return f;
    }

    protected void Own(IDisposable d) => _disposables.Add(d);

    protected async Task Call<T>(string command, T input) where T : IBinaryConvertible, new()
    {
        var r = await Commands.CallAsync(Mesh.Node, EquipmentIds.Command(Kind, Id, command), input);
        Message = r.Ok.Value ? "" : r.Error.Text;
    }

    protected Task Call(string command) => Call(command, NOTESVoid.Void);

    /// <summary>Connect or disconnect, depending on the current state.</summary>
    public Task ToggleConnectAsync() => Call("Connect", (BinaryConvertibleBool)!Connected);

    public virtual void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _disposables.Clear();
    }
}
