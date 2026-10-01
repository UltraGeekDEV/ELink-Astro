using ELink.Contracts.Indi;
using ELink.Indi.Client;
using Event.CoreFunctionality;

namespace ELink.IndiBridge;

/// <summary>One INDI server, bridged both ways onto the mesh and kept alive: connects, attaches the generic mirror
/// and the typed adapters, and when the connection drops tears them down and tries again with back-off.</summary>
public sealed class IndiServerLink : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _name, _host;
    private readonly int _port;
    private readonly IndiGenericPublisher _generic;
    private readonly IndiEquipmentManager _equipment;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IndiServerLink(TypeSafeEVentNode node, DeviceDirectory directory, string serverName, string host = "localhost", int port = 7624,
        Func<string, string>? idFor = null)
    {
        _node = node; _name = serverName; _host = host; _port = port;
        _generic = new IndiGenericPublisher(node, serverName);
        directory.AddServer(serverName);
        _equipment = new IndiEquipmentManager(node, directory, serverName, idFor);
    }

    public string Name => _name;
    public bool IsConnected { get; private set; }

    public async Task StartAsync()
    {
        await _generic.StartAsync();
        _loop = Task.Run(Run);
    }

    private async Task Run()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            string error = "";
            await using var client = new IndiClient(_host, _port);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += c => { if (c is Disconnected) lost.TrySetResult(); return Task.CompletedTask; };
            try
            {
                await _generic.Attach(client);
                await _equipment.Attach(client);
                await client.ConnectAsync(_cts.Token);
                IsConnected = true;
                delay = TimeSpan.FromSeconds(1);
                await PublishLink(true, "");
                using var _ = _cts.Token.Register(() => lost.TrySetResult());
                await lost.Task;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex.Message; }

            IsConnected = false;
            try { await _equipment.Detach(); await _generic.Detach(); } catch (Exception ex) { Console.Error.WriteLine($"[{_name}] detach: {ex.Message}"); }
            if (_cts.IsCancellationRequested) break;
            await PublishLink(false, error);
            try { await Task.Delay(delay, _cts.Token); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    private async Task PublishLink(bool connected, string error)
    {
        try
        {
            await _node.FireEventAsync(IndiIds.Link(_name), new IndiLinkState
            { Connected = connected, Server = _name, Endpoint = $"{_host}:{_port}", Error = error });
        }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(3000));
        await _equipment.DisposeAsync();
        await _generic.DisposeAsync();
    }
}
