using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Core;

/// <summary>The functions an endpoint provides; unregistered together when it goes away.</summary>
public sealed class CommandSet : IDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly List<Action> _undo = new();

    public CommandSet(TypeSafeEVentNode node) { _node = node; }

    public async Task AddAsync<TIn, TOut>(string id, Func<TIn, Task<TOut>> handler, string description)
        where TIn : IBinaryConvertible, new() where TOut : IBinaryConvertible, new()
    {
        await _node.RegisterFunctionAsync(id, handler, description);
        _undo.Add(() => _node.UnregisterFunction(id, handler));
    }

    public void Dispose()
    {
        foreach (var u in _undo) { try { u(); } catch (ObjectDisposedException) { } }
        _undo.Clear();
    }
}
