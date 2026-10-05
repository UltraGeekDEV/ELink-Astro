# Core API

## TypeSafeEVentNode (namespace `Event.CoreFunctionality`) — the normal entry point

```csharp
var node = new TypeSafeEVentNode("Kitchen",
    new TCPServer(IPAddress.Any, 5698, "Kitchen"),   // listen on TCP 5698, answer discovery for "Kitchen"
    new SerialServer("Kitchen"));                     // optional: also accept serial devices
```

The constructor starts everything and blocks briefly until the node is ready (`StartupTimeout`, 10 s → `TimeoutException`). Every network operation has an `…Async` form (holds no thread) and a blocking form. All take an optional `description` on registration and `timeout`/`CancellationToken` on sends.

| Member | Result |
|---|---|
| `HookEventAsync<T>(id, Action<T> or Func<T,Task> cb, description?)` | `true` subscribed; `false` type conflict. First hook of an ID proposes `T` as its type. |
| `FireEventAsync<T>(id, value, timeout?, ct?)` | `true` every subscriber handled it (or none exist); `false` only on type mismatch. Waits for handlers (≤ timeout). |
| `PublishEventAsync<T>(id, value, ct?)` | Fire and forget: `true` once the links took the event (no acknowledgements, no waiting for handlers, nothing kept per event); `false` only on type mismatch. In order per publisher and subscriber; a fast publisher is slowed by back-pressure, not buffered. Handler failures and broken links lose events silently. Several times the throughput of `FireEventAsync` for streams of readings. |
| `RegisterFunctionAsync<TIn,TOut>(id, Func<TIn,TOut> or Func<TIn,Task<TOut>>, description?)` | Provide a function; return `null` for "no answer". |
| `CallFunctionAsync<TIn,TOut>(id, input, timeout?, ct?)` | `BinaryConvertibleCollection<TOut>`: one answer per provider, empty if none, `null` on type mismatch. Missing answers after the timeout are left out. |
| `UnhookEvent(id, cb)`, `UnregisterFunction(id, handler)` | Remove by the same delegate instance (keep it in a variable). The agreed type stays. |
| `TryEstablishConnectionAsync(Package info)` | `MergeResult`: `Connected`, `AlreadyConnected` (would close a loop), `Busy`, `TypeConflict`, `NotNOTES`, `Unreachable`, `Timeout`. Pauses typed traffic in *this* network briefly while descriptors align. |
| `OnLinkFault(Action<LinkFault>)` | A link was dropped for malformed data. `fault.Redial` is the connection info to re-join with (dialing side only); re-join from a background task. |
| `Stop()` / `Dispose()` | Leave the network. Later calls throw `ObjectDisposedException`. |
| `GetEndpointAsync(id)` and `*DynamicAsync` | NON dynamic API, see non-and-scripting.md. |

Connection info packages:

```csharp
new Package(ConnectionTypes.TCPDiscovery, "Kitchen")                                  // UDP multicast discovery by server ID
new Package(ConnectionTypes.TCP, new TCPConnectionData(new IPEndPoint(ip, 5698)))     // direct; SourcePort picks which of our TCP servers dials
```

Process-wide statics: `EVentNode.FunctionCallTimeout` (30 s; also bounds typed `FireEvent`), `TypeSafeEVentNode.ProposalTimeToLive` (5 s), `MergeStepTimeout` (5 s), `MaxMergeAttempts` (6), `StartupTimeout` (10 s), `EVentNode.LinkFaultFlushTimeout` (2 s).

Callbacks and handlers run as thread-pool tasks, never on a link's reader; an exception in one is logged and that subscriber/provider contributes nothing. Deliveries can arrive in any order.

## EVentNode (namespace `EVent.CoreFunctionality`) — the raw router

Use for raw bytes, tooling, bridging, or high-rate fire-and-forget without acknowledgements. `TypeSafeEVentNode` hides its `EVentNode`; to mix raw and typed traffic, run a separate `EVentNode` in the same network.

```csharp
var node = new EVentNode("Raw", new TCPServer(IPAddress.Any, 5700, "Raw"));
node.Run();                                              // start transports
var local = node.AddLocalCallbackLayer();                // LocalCallbackClient: your code as a client
local.HookEvent<BinaryConvertibleString>("chat", s => Console.WriteLine(s.Text));
local.FireEvent("chat", (BinaryConvertibleString)"hi");  // only sent if someone is interested
local.RegisterFunction<NOTESVoid, BinaryConvertibleFloat>("GetCpuLoad", _ => 0.3f);
var loads = node.CallFunction<BinaryConvertibleFloat, NOTESVoid>("GetCpuLoad", NOTESVoid.Void);   // <TResult, TInput>!
```

- Raw events use the plain (non-NOTES) layout and have **no type checking**: a payload that doesn't parse is dropped silently.
- `OnDataReceived`, `OnEventAdded`, `OnEventRemoved`, `OnConnected`, `OnLinkFault` observe traffic. `GetEvents()` lists routed IDs (prefix routes included).
- Raw links (`AcceptClient`, `CreateInterconnect`) do no loop check: keep the graph a tree yourself.
- `EVentNode.IsPrefixRoute("A.*")`, `EVentNode.CoveringPrefixRoutes("A.B.C")` → `A.*`, `A.B.*`.

## Transports

- `TCPServer(IPAddress listen, int port, params string[] discoverableIDs)`: accepts links, dials `TCP`/`TCPDiscovery` connection infos, answers multicast discovery (239.255.12.85:8563) for the given IDs.
- `SerialServer(string serverID)` (namespace `Event.Connections.Serial`): scans serial ports; can't dial. Serial has known issues (links drop after ~1 s of silence, 250-byte receive buffer).
- A node can have several transports; links over different media form one network.
- A custom transport implements `ICommsProtocol` (`OnClientAccepted`, `Run`, `Stop`, `EstablishInterconnect(Package info)` returning a `QueuedClient` or null if the info isn't for it) and `IStreamClient` (`Send(byte[] frame)`, `ReadPackage()` returning null at end of stream, `Close()`). `assets/MemTransport.cs` is a complete, small example. Connection info is a `Package` whose `EventID` names the connection type and whose data carries its parameters.
