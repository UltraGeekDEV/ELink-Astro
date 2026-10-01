using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Core;

/// <summary>Publishes a state value on its event whenever it really changes, and answers GetState for late joiners.
/// Publishing is serialised, so subscribers see states in order.</summary>
public sealed class StatePublisher<T> : IDisposable where T : IBinaryConvertible, new()
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _stateId, _getStateId;
    private readonly Func<T> _build;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<NOTESVoid, Task<T>> _getState;
    private byte[]? _last;
    private bool _started;

    public StatePublisher(TypeSafeEVentNode node, string stateId, string getStateId, Func<T> build)
    {
        _node = node; _stateId = stateId; _getStateId = getStateId; _build = build;
        _getState = _ => Task.FromResult(_build());
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        await _node.RegisterFunctionAsync(_getStateId, _getState, "the current state, for consumers that join after the last state event");
        await PublishAsync();
    }

    /// <summary>Rebuilds the state; fires the event only if it differs from the last one published.</summary>
    public async Task PublishAsync()
    {
        await _lock.WaitAsync();
        try
        {
            T state = _build();
            byte[] bytes = state.ToBytes();
            if (_last is not null && bytes.AsSpan().SequenceEqual(_last)) return;
            _last = bytes;
            try { await _node.FireEventAsync(_stateId, state); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[state] {_stateId}: {ex.Message}"); }
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try { _node.UnregisterFunction(_getStateId, _getState); } catch (ObjectDisposedException) { }
    }
}
