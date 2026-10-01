using System.Net;
using System.Net.Sockets;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;

namespace ELink.Stellarium;

/// <summary>Makes an ELink Pointer (a mount pointer, a smart scope, a scope of scopes) a telescope Stellarium can connect to:
/// Stellarium shows its reticle where the pointer points, and Stellarium's "slew to" moves the pointer. The pointer is known only
/// by id; it can be rebound while running.</summary>
public sealed class StellariumTelescopeServer : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<NetworkStream> _clients = new();
    private RemoteState<PointerState>? _pointer;
    private string _pointerId = "";
    private Task? _accept, _pump;

    /// <summary>How often the position is sent even when it did not change (Stellarium drops a silent telescope).</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromMilliseconds(500);
    public event Action? ClientsChanged;
    public event Action<string>? Problem;

    public StellariumTelescopeServer(TypeSafeEVentNode node, int port = 10001, IPAddress? listen = null)
    {
        _node = node;
        _listener = new TcpListener(listen ?? IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int ClientCount { get { lock (_gate) return _clients.Count; } }
    public string PointerId { get { lock (_gate) return _pointerId; } }

    public async Task StartAsync(string pointerId)
    {
        _listener.Start();
        await BindAsync(pointerId);
        _accept = Task.Run(AcceptLoop);
        _pump = Task.Run(PumpLoop);
    }

    /// <summary>Show and slew a different pointer from now on.</summary>
    public async Task BindAsync(string pointerId)
    {
        RemoteState<PointerState>? old;
        var state = pointerId == "" ? null : new RemoteState<PointerState>(_node, PointerIds.State(pointerId), PointerIds.GetState(pointerId));
        if (state is not null) await state.StartAsync();
        lock (_gate) { old = _pointer; _pointer = state; _pointerId = pointerId; }
        old?.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            client.NoDelay = true;
            var stream = client.GetStream();
            lock (_gate) _clients.Add(stream);
            ClientsChanged?.Invoke();
            _ = Task.Run(() => ReadLoop(client, stream));
        }
    }

    private async Task ReadLoop(TcpClient client, NetworkStream stream)
    {
        var buffer = new byte[4096]; int filled = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(filled), _cts.Token);
                if (n == 0) break;
                filled += n;
                int used;
                while ((used = TelescopeProtocol.TryParse(buffer.AsSpan(0, filled), out ushort type, out double ra, out double dec)) > 0)
                {
                    if (type == 0 && used >= TelescopeProtocol.GotoLength) await GotoAsync(ra, dec);
                    Buffer.BlockCopy(buffer, used, buffer, 0, filled - used);
                    filled -= used;
                }
                if (filled == buffer.Length) filled = 0;                      // a runaway message: drop it
            }
        }
        catch (Exception) { /* client went away */ }
        finally
        {
            lock (_gate) _clients.Remove(stream);
            client.Dispose();
            ClientsChanged?.Invoke();
        }
    }

    private async Task GotoAsync(double raHours, double decDegrees)
    {
        string id = PointerId;
        if (id == "") { Problem?.Invoke("Stellarium asked for a slew but no pointer is bound"); return; }
        var r = await Commands.CallAsync(_node, PointerIds.Goto(id), new SkyTarget { RaHours = raHours, DecDegrees = decDegrees, Epoch = "J2000" });
        if (!r.Ok.Value) Problem?.Invoke("slew from Stellarium refused: " + r.Error.Text);
    }

    /// <summary>Sends the pointer's position to every client, on a heartbeat (the protocol expects regular updates).</summary>
    private async Task PumpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(Heartbeat, _cts.Token); } catch (OperationCanceledException) { return; }
            PointerState? s; List<NetworkStream> clients;
            lock (_gate) { s = _pointer?.Latest; clients = _clients.ToList(); }
            if (s is null || s.Phase.Text == "Disconnected" || clients.Count == 0) continue;
            var msg = TelescopeProtocol.Position(s.RaHours.Value, s.DecDegrees.Value, s.Phase.Text == "Error" ? 1 : 0);
            foreach (var c in clients)
            {
                try { await c.WriteAsync(msg, _cts.Token); } catch (Exception) { /* the read loop will drop it */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_gate) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
        if (_accept is not null) await Task.WhenAny(_accept, Task.Delay(1000));
        if (_pump is not null) await Task.WhenAny(_pump, Task.Delay(1000));
        _pointer?.Dispose();
    }
}
