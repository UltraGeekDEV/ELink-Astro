using ELink.Core;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.Infrastructure;

/// <summary>Follows a remote state (event + GetState) and applies each value on the UI thread: how a view model
/// mirrors a device without knowing anything about it but the IDs and the contract type.</summary>
public sealed class Follower<T> : IDisposable where T : IBinaryConvertible, new()
{
    private readonly RemoteState<T> _state;

    public Follower(TypeSafeEVentNode node, string stateId, string getStateId, Action<T> apply)
    {
        _state = new RemoteState<T>(node, stateId, getStateId);
        _state.Changed += s => UiThread.Post(() => apply(s));
    }

    public T? Latest => _state.Latest;
    public Task StartAsync() => _state.StartAsync();
    public void Dispose() => _state.Dispose();
}
