using ELink.Contracts.Indi;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using System.Collections.Immutable;
using System.Globalization;

namespace ELink.IndiBridge;

/// <summary>The generic half of the INDI to EVent translation: mirrors every property of every device of one
/// INDI server onto EVent (events for changes, functions to read a snapshot and to change properties), without
/// any knowledge of the drivers. Typed adapters sit on top of the same <see cref="IndiClient"/>.
/// The client can be swapped (<see cref="Attach"/>/<see cref="Detach"/>) so a reconnect keeps the EVent endpoints.</summary>
public sealed class IndiGenericPublisher : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _server;
    private IndiClient? _client;
    private Func<IndiChange, Task>? _handler;
    private bool _started;

    public IndiGenericPublisher(TypeSafeEVentNode node, string serverName)
    {
        _node = node;
        _server = serverName;
    }

    public string ServerName => _server;

    /// <summary>Register the functions; call once.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        await _node.RegisterFunctionAsync<NOTESVoid, IndiSnapshot>(IndiIds.Snapshot(_server), _ => Task.FromResult(BuildSnapshot()),
            "all INDI properties the bridge currently knows, for consumers that join late");
        await _node.RegisterFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set(_server), SetAsync,
            "ask an INDI device to change a property (newXXXVector); Ok means the request was sent");
        await _node.RegisterFunctionAsync<IndiBlobRequest, IndiResult>(IndiIds.EnableBlob(_server), EnableBlobAsync,
            "start or stop receiving the BLOBs (images) of an INDI device");
    }

    public async Task Attach(IndiClient client)
    {
        await Detach();
        _client = client;
        _handler = OnChange;
        client.Changed += _handler;
        // Properties already in the model (attach after connect): announce them.
        foreach (var p in client.Properties) await PublishProperty("Defined", p, "");
    }

    public async Task Detach()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;
        client.Changed -= _handler;
        foreach (var device in client.Devices) await Fire(IndiIds.Property(_server), Event("Deleted", IndiMap.Tombstone(device, ""), ""));
    }

    private IndiSnapshot BuildSnapshot()
    {
        var snap = new IndiSnapshot { Connected = _client?.IsConnected == true, Server = _server };
        if (_client is not null) foreach (var p in _client.Properties) snap.Properties.Add(IndiMap.ToInfo(p));
        return snap;
    }

    private async Task<IndiResult> SetAsync(IndiSetRequest request)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return IndiResult.Fail("not connected to the INDI server");
        var property = client.GetProperty(request.Device.Text, request.Property.Text);
        if (property is null) return IndiResult.Fail($"unknown property {request.Device.Text}/{request.Property.Text}");
        if (property.Perm == IndiPerm.ReadOnly) return IndiResult.Fail("property is read-only");
        if (property.Type is IndiPropertyType.Light or IndiPropertyType.Blob) return IndiResult.Fail($"{property.Type} properties cannot be set");

        var elements = ImmutableArray.CreateBuilder<IndiNewElement>();
        foreach (var e in request.Elements)
        {
            string id = e.Id.Text;
            if (!property.Has(id)) return IndiResult.Fail($"unknown element {id}");
            string value = e.Value.Text;
            switch (property.Type)
            {
                case IndiPropertyType.Number:
                    double n = IndiNumber.Parse(value);
                    if (double.IsNaN(n)) return IndiResult.Fail($"not a number: {value}");
                    value = IndiNumber.ToWire(n);
                    break;
                case IndiPropertyType.Switch:
                    if (!(value.Equals("On", StringComparison.OrdinalIgnoreCase) || value.Equals("Off", StringComparison.OrdinalIgnoreCase)))
                        return IndiResult.Fail($"switch value must be On or Off, got {value}");
                    value = value.Equals("On", StringComparison.OrdinalIgnoreCase) ? "On" : "Off";
                    break;
            }
            elements.Add(new IndiNewElement(id, value));
        }
        if (elements.Count == 0) return IndiResult.Fail("no elements");
        try { await client.SendAsync(new IndiNew(property.Type, property.Device, property.Name, elements.ToImmutable())); }
        catch (Exception ex) { return IndiResult.Fail(ex.Message); }
        return IndiResult.Success();
    }

    private async Task<IndiResult> EnableBlobAsync(IndiBlobRequest request)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return IndiResult.Fail("not connected to the INDI server");
        try { await client.EnableBlobAsync(request.Device.Text, request.Enabled.Value ? IndiBlobMode.Also : IndiBlobMode.Never); }
        catch (Exception ex) { return IndiResult.Fail(ex.Message); }
        return IndiResult.Success();
    }

    private async Task OnChange(IndiChange change)
    {
        switch (change)
        {
            case PropertyDefined d: await PublishProperty("Defined", d.Property, d.Message ?? ""); break;
            case PropertyUpdated u:
                await PublishProperty("Updated", u.Property, u.Message ?? "");
                if (u.Property.Type == IndiPropertyType.Blob) await PublishBlobs(u.Property);
                break;
            case PropertyDeleted p: await Fire(IndiIds.Property(_server), Event("Deleted", IndiMap.Tombstone(p.Device, p.Name), "")); break;
            case DeviceDeleted dd: await Fire(IndiIds.Property(_server), Event("Deleted", IndiMap.Tombstone(dd.Device, ""), "")); break;
            case LogReceived l:
                await Fire(IndiIds.Log(_server), new IndiLogEvent
                {
                    Device = l.Device ?? "", Text = l.Text,
                    Timestamp = l.Timestamp?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                });
                break;
        }
    }

    private Task PublishProperty(string kind, IndiProperty p, string message) =>
        Fire(IndiIds.Property(_server), Event(kind, IndiMap.ToInfo(p), message));

    private async Task PublishBlobs(IndiProperty p)
    {
        foreach (var e in p.Elements.Where(e => e.Blob is { Length: > 0 }))
        {
            await Fire(IndiIds.Blob(_server), new IndiBlobEvent
            {
                Device = p.Device, Property = p.Name, Element = e.Name, Format = e.BlobFormat ?? "", Data = new ELink.Contracts.RawBytes(e.Blob!),
            });
        }
    }

    private static IndiPropertyEvent Event(string kind, IndiPropertyInfo info, string message) =>
        new() { Kind = kind, Message = message, Info = info };

    private async Task Fire<T>(string id, T value) where T : EVent.Connections.Models.BaseBinaryConvertibles.IBinaryConvertible, new()
    {
        try { await _node.FireEventAsync(id, value); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[indi-evt] fire {id} failed: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync() => await Detach();
}
