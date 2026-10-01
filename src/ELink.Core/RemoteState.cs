using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Core;

/// <summary>The latest value of some remote thing, kept current from its state event. A late joiner is seeded from the
/// thing's GetState function, because state events fire on change only. This is the building block for anything that
/// follows a device (smart scopes, sequencers, UI view models): it knows only IDs and contract types.</summary>
public sealed class RemoteState<T> : IDisposable where T : IBinaryConvertible, new()
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _stateEventId, _getStateId;
    private readonly Action<T> _callback;
    private readonly object _gate = new();
    private readonly List<(Func<T, bool> predicate, TaskCompletionSource<T> tcs)> _waiters = new();
    private T? _latest;
    private bool _hooked;

    public RemoteState(TypeSafeEVentNode node, string stateEventId, string getStateId)
    {
        _node = node; _stateEventId = stateEventId; _getStateId = getStateId;
        _callback = Update;
    }

    public T? Latest { get { lock (_gate) return _latest; } }

    /// <summary>Raised (on a thread-pool thread) for every new value, including the seed.</summary>
    public event Action<T>? Changed;

    public async Task StartAsync()
    {
        if (_hooked) return;
        _hooked = true;
        await _node.HookEventAsync(_stateEventId, _callback, "state follower");
        try
        {
            var seed = await _node.CallFunctionAsync<NOTESVoid, T>(_getStateId, NOTESVoid.Void);
            if (seed is { Count: > 0 } && Latest is null) Update(seed[0]);
        }
        catch (Exception) { /* nobody provides it (yet): the first state event will fill it */ }
    }

    private void Update(T value)
    {
        List<TaskCompletionSource<T>> ready = new();
        lock (_gate)
        {
            _latest = value;
            for (int i = _waiters.Count - 1; i >= 0; i--)
                if (_waiters[i].predicate(value)) { ready.Add(_waiters[i].tcs); _waiters.RemoveAt(i); }
        }
        foreach (var t in ready) t.TrySetResult(value);
        Changed?.Invoke(value);
    }

    /// <summary>Completes with the first value (now or later) that satisfies the predicate.</summary>
    public async Task<T> WaitAsync(Func<T, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_latest is not null && predicate(_latest)) return _latest;
            _waiters.Add((predicate, tcs));
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task.ConfigureAwait(false); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"timed out waiting on {_stateEventId}"); }
        finally { lock (_gate) _waiters.RemoveAll(w => w.tcs == tcs); }
    }

    public void Dispose()
    {
        if (!_hooked) return;
        _hooked = false;
        try { _node.UnhookEvent(_stateEventId, _callback); } catch (ObjectDisposedException) { }
    }
}
