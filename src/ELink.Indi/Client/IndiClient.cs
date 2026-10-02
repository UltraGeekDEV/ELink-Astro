using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ELink.Indi.Protocol;

namespace ELink.Indi.Client;

/// <summary>What changed in the client's model. Delivered in order on a dedicated dispatcher, so handlers may
/// block or await (e.g. fire EVent events) without stalling the socket reader.</summary>
public abstract record IndiChange;
public sealed record PropertyDefined(IndiProperty Property, string? Message) : IndiChange;
/// <summary>Property after the update was merged. Blob payloads are in the property's elements.</summary>
public sealed record PropertyUpdated(IndiProperty Property, string? Message) : IndiChange;
public sealed record PropertyDeleted(string Device, string Name) : IndiChange;
public sealed record DeviceDeleted(string Device) : IndiChange;
public sealed record LogReceived(string? Device, DateTime? Timestamp, string Text) : IndiChange;
public sealed record Disconnected(Exception? Error) : IndiChange;

/// <summary>A client for one indiserver: keeps a live, thread-safe model of every device and property.</summary>
public sealed class IndiClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, IndiProperty> _properties = new();
    private readonly Channel<IndiChange> _changes = Channel.CreateUnbounded<IndiChange>(new() { SingleReader = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader, _dispatcher;
    private int _disposed;

    public IndiClient(string host = "localhost", int port = 7624) { _host = host; _port = port; }

    public string Host => _host;
    public int Port => _port;
    public bool IsConnected => _tcp?.Connected == true && !_cts.IsCancellationRequested;

    /// <summary>Raised on the dispatcher, in server order.</summary>
    public event Func<IndiChange, Task>? Changed;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _dispatcher = Task.Run(DispatchLoop);
        _reader = Task.Run(ReadLoop);
        await SendAsync(new IndiGetProperties(), ct).ConfigureAwait(false);
    }

    // ---- model -----------------------------------------------------------------------------------------------

    public IReadOnlyCollection<IndiProperty> Properties => _properties.Values.ToArray();
    public IReadOnlyList<string> Devices => _properties.Values.Select(p => p.Device).Distinct().Order().ToArray();
    public IEnumerable<IndiProperty> PropertiesOf(string device) => _properties.Values.Where(p => p.Device == device);
    public IndiProperty? GetProperty(string device, string name) => _properties.GetValueOrDefault(device + "/" + name);

    // ---- commands --------------------------------------------------------------------------------------------

    public async Task SendAsync(IndiCommand command, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        byte[] bytes = Encoding.UTF8.GetBytes(IndiCodec.Serialize(command));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public Task GetPropertiesAsync(string? device = null, string? name = null, CancellationToken ct = default) =>
        SendAsync(new IndiGetProperties(device, name), ct);

    public Task EnableBlobAsync(string device, IndiBlobMode mode = IndiBlobMode.Also, string? name = null, CancellationToken ct = default) =>
        SendAsync(new IndiEnableBlob(device, name, mode), ct);

    public Task SetSwitchAsync(string device, string property, string element, bool on = true, CancellationToken ct = default) =>
        SendAsync(new IndiNew(IndiPropertyType.Switch, device, property,
            ImmutableArray.Create(new IndiNewElement(element, on ? "On" : "Off"))), ct);

    public Task SetNumbersAsync(string device, string property, IEnumerable<(string element, double value)> values, CancellationToken ct = default) =>
        SendAsync(new IndiNew(IndiPropertyType.Number, device, property,
            values.Select(v => new IndiNewElement(v.element, IndiNumber.ToWire(v.value))).ToImmutableArray()), ct);

    public Task SetNumberAsync(string device, string property, string element, double value, CancellationToken ct = default) =>
        SetNumbersAsync(device, property, new[] { (element, value) }, ct);

    public Task SetTextsAsync(string device, string property, IEnumerable<(string element, string value)> values, CancellationToken ct = default) =>
        SendAsync(new IndiNew(IndiPropertyType.Text, device, property,
            values.Select(v => new IndiNewElement(v.element, v.value)).ToImmutableArray()), ct);

    public Task SetTextAsync(string device, string property, string element, string value, CancellationToken ct = default) =>
        SendAsync(new IndiNew(IndiPropertyType.Text, device, property,
            ImmutableArray.Create(new IndiNewElement(element, value))), ct);

    /// <summary>Waits until a property satisfies a predicate (checked now and on every change).</summary>
    public async Task<IndiProperty> WaitForAsync(string device, string name, Func<IndiProperty, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IndiProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IndiChange, Task> handler = c =>
        {
            if (c is PropertyDefined { Property: var p } && p.Device == device && p.Name == name && predicate(p)) tcs.TrySetResult(p);
            else if (c is PropertyUpdated { Property: var q } && q.Device == device && q.Name == name && predicate(q)) tcs.TrySetResult(q);
            return Task.CompletedTask;
        };
        Changed += handler;
        try
        {
            if (GetProperty(device, name) is { } now && predicate(now)) return now;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using var _ = linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { Changed -= handler; }
    }

    // ---- loops -----------------------------------------------------------------------------------------------

    private async Task ReadLoop()
    {
        Exception? error = null;
        try
        {
            await foreach (var message in IndiCodec.ReadAsync(_stream!, _cts.Token).ConfigureAwait(false))
                Apply(message);
        }
        catch (Exception e) when (e is not OperationCanceledException) { error = e; }
        _changes.Writer.TryWrite(new Disconnected(error));
        _changes.Writer.TryComplete();
    }

    private void Apply(IndiMessage message)
    {
        switch (message)
        {
            case IndiDefine d:
                _properties[d.Property.Key] = d.Property;
                Publish(new PropertyDefined(d.Property, d.Message));
                break;
            case IndiSet s:
                if (!_properties.TryGetValue(s.Device + "/" + s.Name, out var old)) break; // set before def: ignore
                var merged = Merge(old, s);
                _properties[merged.Key] = merged;
                Publish(new PropertyUpdated(merged, s.Message));
                break;
            case IndiDelete x:
                if (x.Name is null)
                {
                    foreach (var key in _properties.Keys.Where(k => k.StartsWith(x.Device + "/", StringComparison.Ordinal)))
                        _properties.TryRemove(key, out _);
                    Publish(new DeviceDeleted(x.Device));
                }
                else if (_properties.TryRemove(x.Device + "/" + x.Name, out _))
                    Publish(new PropertyDeleted(x.Device, x.Name));
                break;
            case IndiLog l:
                Publish(new LogReceived(l.Device, l.Timestamp, l.Text));
                break;
        }
    }

    private static int IndexOf(ImmutableArray<IndiElement>.Builder b, string name)
    {
        for (int i = 0; i < b.Count; i++) if (b[i].Name == name) return i;
        return -1;
    }

    private static IndiProperty Merge(IndiProperty old, IndiSet set)
    {
        var elements = old.Elements.ToBuilder();
        foreach (var u in set.Updates)
        {
            int i = IndexOf(elements, u.Name);
            if (i < 0) continue;
            elements[i] = u.Blob is not null
                ? elements[i] with { Value = u.Value, Blob = u.Blob, BlobFormat = u.BlobFormat }
                : elements[i] with { Value = u.Value };
        }
        return old with
        {
            State = set.State ?? old.State,
            Timeout = set.Timeout ?? old.Timeout,
            Timestamp = set.Timestamp ?? old.Timestamp,
            Elements = elements.ToImmutable(),
        };
    }

    private void Publish(IndiChange change) => _changes.Writer.TryWrite(change);

    private async Task DispatchLoop()
    {
        await foreach (var change in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var handlers = Changed;
            if (handlers is null) continue;
            foreach (Func<IndiChange, Task> h in handlers.GetInvocationList())
            {
                try { await h(change).ConfigureAwait(false); }
                catch (Exception e) { Console.Error.WriteLine($"[indi] handler failed: {e.Message}"); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { _tcp?.Close(); } catch { /* already closed */ }
        if (_reader is not null) await Task.WhenAny(_reader, Task.Delay(1000)).ConfigureAwait(false);
        _changes.Writer.TryComplete();
        if (_dispatcher is not null) await Task.WhenAny(_dispatcher, Task.Delay(1000)).ConfigureAwait(false);
    }
}
