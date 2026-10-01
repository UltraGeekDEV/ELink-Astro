using ELink.Contracts.Atlas;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Stellarium;

/// <summary>Stellarium on the mesh: the telescope server for one pointer, plus Remote Control (show a target, read and announce
/// the selection). Either half works without the other; Stellarium does not have to be running.</summary>
public sealed class StellariumService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly StellariumTelescopeServer _telescope;
    private readonly StellariumRemote _remote;
    private readonly CommandSet _commands;
    private readonly StatePublisher<StellariumState> _publisher;
    private readonly CancellationTokenSource _cts = new();
    private bool _reachable;
    private string _message = "", _lastSelection = "";
    private Task? _poll;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public StellariumService(TypeSafeEVentNode node, int telescopePort = 10001, string remoteUrl = "http://127.0.0.1:8090", HttpMessageHandler? handler = null)
    {
        _node = node;
        _telescope = new StellariumTelescopeServer(node, telescopePort);
        _remote = new StellariumRemote(remoteUrl, handler);
        _commands = new CommandSet(node);
        _publisher = new(node, StellariumIds.State, StellariumIds.GetState, () => new StellariumState
        {
            TelescopePort = _telescope.Port, TelescopeClients = _telescope.ClientCount, PointerId = _telescope.PointerId,
            RemoteUrl = _remote.BaseUrl, RemoteReachable = _reachable, Message = _message,
        });
        _telescope.ClientsChanged += () => _ = _publisher.PublishAsync();
        _telescope.Problem += m => { _message = m; _ = _publisher.PublishAsync(); };
    }

    public StellariumTelescopeServer Telescope => _telescope;

    public async Task StartAsync(string pointerId = "")
    {
        await _telescope.StartAsync(pointerId);
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(StellariumIds.BindPointer, async id =>
        {
            await _telescope.BindAsync(id.Text);
            await _publisher.PublishAsync();
            return CommandResult.Success();
        }, "which pointer Stellarium shows and slews");
        await _commands.AddAsync<SkyTarget, CommandResult>(StellariumIds.Show, ShowAsync, "centre Stellarium's view on a position");
        await _commands.AddAsync<NOTESVoid, AtlasHit>(StellariumIds.GetSelection, async _ => await SelectionAsync() ?? new AtlasHit(), "what is selected in Stellarium");
        await _publisher.StartAsync();
        _poll = Task.Run(PollLoop);
    }

    private async Task<CommandResult> ShowAsync(SkyTarget t)
    {
        double ra = t.RaHours.Value, dec = t.DecDegrees.Value;
        if (t.Epoch.Text == "JNow") (ra, dec) = ELink.Core.Astro.Precession.DateToJ2000(ra, dec, DateTime.UtcNow);
        try { await _remote.ShowAsync(ra, dec); return CommandResult.Success(); }
        catch (Exception ex) { return CommandResult.Fail($"Stellarium Remote Control is not reachable at {_remote.BaseUrl} ({ex.Message})"); }
    }

    private async Task<AtlasHit?> SelectionAsync()
    {
        try
        {
            var s = await _remote.GetSelectionAsync();
            return s is null ? null : new AtlasHit { Label = s.Name, Kind = s.Kind, RaHours = s.RaHours, DecDegrees = s.DecDegrees, Magnitude = (float)s.Magnitude };
        }
        catch (Exception) { return null; }
    }

    /// <summary>Checks Stellarium is there and announces selection changes.</summary>
    private async Task PollLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            bool reachable = await _remote.PingAsync(_cts.Token);
            if (reachable != _reachable) { _reachable = reachable; await _publisher.PublishAsync(); }
            if (reachable && await SelectionAsync() is { } hit)
            {
                string key = $"{hit.Label.Text}|{hit.RaHours.Value:0.0000}|{hit.DecDegrees.Value:0.000}";
                if (key != _lastSelection)
                {
                    _lastSelection = key;
                    try { await _node.FireEventAsync(StellariumIds.Selected, hit); } catch (ObjectDisposedException) { }
                }
            }
            try { await Task.Delay(PollInterval, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_poll is not null) await Task.WhenAny(_poll, Task.Delay(2000));
        _commands.Dispose(); _publisher.Dispose();
        await _telescope.DisposeAsync();
        _remote.Dispose();
    }
}
