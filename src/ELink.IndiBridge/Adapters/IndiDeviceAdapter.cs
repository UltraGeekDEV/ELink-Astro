using ELink.Contracts.Equipment;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

/// <summary>What an adapter needs from its surroundings.</summary>
public sealed record AdapterContext(TypeSafeEVentNode Node, IndiClient Client, string Server, string Device, string Id);

/// <summary>Non-generic face of an adapter, for the code that manages them.</summary>
public interface IEquipmentAdapter : IAsyncDisposable
{
    string Kind { get; }
    DeviceInfo Info { get; }
    Task StartAsync();
}

/// <summary>Turns one INDI device into one ELink equipment kind: tracks the device's properties, publishes a state
/// event when the typed state really changes, and offers the kind's command functions. Subclasses only say how to
/// build the state and which commands exist.</summary>
public abstract class IndiDeviceAdapter<TState> : IEquipmentAdapter where TState : IBinaryConvertible, new()
{
    private readonly AdapterContext _ctx;
    private readonly List<Func<Task>> _unregister = new();
    private Func<IndiChange, Task>? _handler;
    private byte[]? _lastState;
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    protected IndiDeviceAdapter(AdapterContext ctx) { _ctx = ctx; }

    public abstract string Kind { get; }
    public string Device => _ctx.Device;
    public string Id => _ctx.Id;
    protected IndiClient Client => _ctx.Client;
    protected TypeSafeEVentNode Node => _ctx.Node;

    public DeviceInfo Info => new() { Id = Id, Kind = Kind, DisplayName = Device, Source = "indi:" + _ctx.Server };

    protected abstract TState BuildState();
    protected abstract Task RegisterCommandsAsync();

    /// <summary>Hook for properties that are events rather than state (e.g. camera frames).</summary>
    protected virtual Task OnPropertyChangedAsync(IndiChange change) => Task.CompletedTask;

    public async Task StartAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("Connect", ConnectAsync, "connect or disconnect the device");
        await RegisterCommandAsync<NOTESVoid, TState>("GetState", _ => Task.FromResult(BuildState()), "the current state, for consumers that join after the last state event");
        await RegisterCommandsAsync();
        _handler = OnChange;
        Client.Changed += _handler;
        await PublishStateAsync();
    }

    protected string StateId => EquipmentIds.State(Kind, Id);

    // ---- helpers for subclasses -----------------------------------------------------------------------------

    protected IndiProperty? P(string name) => Client.GetProperty(Device, name);

    protected bool Connected => P("CONNECTION")?.Switch("CONNECT") == true;

    protected async Task RegisterCommandAsync<TIn, TOut>(string command, Func<TIn, Task<TOut>> handler, string description)
        where TIn : IBinaryConvertible, new() where TOut : IBinaryConvertible, new()
    {
        string id = EquipmentIds.Command(Kind, Id, command);
        await Node.RegisterFunctionAsync(id, handler, description);
        _unregister.Add(() => { Node.UnregisterFunction(id, handler); return Task.CompletedTask; });
    }

    /// <summary>Sends INDI changes, turning the usual failure modes into a CommandResult.</summary>
    protected async Task<CommandResult> Send(Func<Task> action)
    {
        if (!Client.IsConnected) return CommandResult.Fail("not connected to the INDI server");
        try { await action(); return CommandResult.Success(); }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    }

    protected CommandResult? Need(string property, string? element = null)
    {
        var p = P(property);
        if (p is null || (element is not null && !p.Has(element))) return CommandResult.Fail($"{Device} has no {property}" + (element is null ? "" : "." + element));
        return null;
    }

    protected Task SetSwitch(string property, string element) => Client.SetSwitchAsync(Device, property, element, true);
    protected Task SetNumber(string property, string element, double value) => Client.SetNumberAsync(Device, property, element, value);

    private async Task<CommandResult> ConnectAsync(BinaryConvertibleBool connect)
    {
        if (Need("CONNECTION") is { } missing) return missing;
        return await Send(() => Client.SetSwitchAsync(Device, "CONNECTION", connect.Value ? "CONNECT" : "DISCONNECT", true));
    }

    // ---- state publication ----------------------------------------------------------------------------------

    private async Task OnChange(IndiChange change)
    {
        bool mine = change switch
        {
            PropertyDefined d => d.Property.Device == Device,
            PropertyUpdated u => u.Property.Device == Device,
            PropertyDeleted p => p.Device == Device,
            _ => false,
        };
        if (!mine) return;
        await OnPropertyChangedAsync(change);
        await PublishStateAsync();
    }

    protected async Task PublishStateAsync()
    {
        await _publishLock.WaitAsync();
        try
        {
            TState state = BuildState();
            byte[] bytes = state.ToBytes();
            if (_lastState is not null && bytes.AsSpan().SequenceEqual(_lastState)) return;
            _lastState = bytes;
            try { await Node.FireEventAsync(StateId, state); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[{Kind}:{Id}] state publish failed: {ex.Message}"); }
        }
        finally { _publishLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_handler is not null) Client.Changed -= _handler;
        foreach (var u in _unregister) { try { await u(); } catch { } }
        _unregister.Clear();
    }

    // ---- value helpers ----------------------------------------------------------------------------------------

    protected static string Phase(bool connected, string whenConnected) => connected ? whenConnected : "Disconnected";
}
