---
name: event
description: Development helper for EVent, a .NET 8 library for an address-free distributed event and RPC mesh with network-wide type agreement (NOTES / NON). Use whenever code uses or should use EVent - TypeSafeEVentNode, HookEvent/FireEvent, RegisterFunction/CallFunction (dRPC), NOTES types (IBinaryConvertible, NOTESDescriptor, RegisterField), NON dynamic objects (NONSchema, NONObject), NamespaceNode, FirewallNode, NetworkDirectory, the EVent.Scripting scripting node, the C++ leaf library (evn::Leaf, EVN_RECORD, evn/leaf.hpp), or an EVent peer written in another language - and whenever programs or devices should exchange typed events or calls without knowing each other's addresses, or two applications should be connected through EVent.
---

# EVent

EVent is a .NET 8 library for a network of cooperating processes (**nodes**) that talk through named **events** and **functions** instead of addresses. Nodes link over TCP (with UDP multicast discovery), serial, or any custom transport; the link graph is a **tree**. Every node learns who is interested in which ID and routes messages only toward interest.

- **Events**: delivered to every subscriber in the network.
- **dRPC functions**: a call reaches *every* provider of the ID and returns the list of *all* their answers (not one target).
- **NOTES** (No Overhead Type Enforcement System): the network agrees one type per ID the first time it is used; payloads carry no type info. The wire format and type descriptions are called **NON** (NOTES Object Notation).
- **Policy layers**: `FirewallNode` (what may cross between two networks), `NamespaceNode` (a network as a named sub-network: inner `X` is `ns.X` outside, routed as one entry).
- **NON dynamic objects**: use any agreed type at run time without a C# class. **Scripting node** (separate `EVent.Scripting` library): C# scripts, compiled with Roslyn, as glue between endpoints, managed from a local web page.
- **Leaves in other languages**: a C++ program is a typed leaf node (`evn::Leaf`, header-only C++17 in `assets/cpp/`) linked to a local C# node, its router (loopback TCP now, shared memory later).

## Adding it to a project

EVent is a library: the core is one assembly (`EVent`, targets `net8.0`, depends on `Crc32.NET` and `System.IO.Ports`); the scripting node is a second one (`EVent.Scripting`, adds Roslyn `Microsoft.CodeAnalysis.CSharp` and the ASP.NET Core shared framework); C++ programs use the header-only leaf library in `assets/cpp/` (see references/native.md). Reference them the way the project already gets EVent (a package or a project reference). If the project doesn't have EVent yet, ask the user how they distribute it; don't guess a path.

The root namespaces are mixed (`Event.*` and `EVent.*`). The usual set:

```csharp
using Event.CoreFunctionality;                          // TypeSafeEVentNode, MergeResult, NOTESFlexibleSerDes
using Event.CoreFunctionality.EVentIDs;                 // ConnectionTypes, NOTESEvents, AdminEvents
using EVent.CoreFunctionality;                          // EVentNode, QueuedClient
using EVent.Connections;                                // ICommsProtocol, PackageType
using EVent.Connections.TCP;                            // TCPServer
using EVent.Connections.Models.BaseBinaryConvertibles;  // Package, IBinaryConvertible, BinaryConvertibleString/Int32/UInt64/Float/Double/Bool/..., Collection
using Event.Connections.Models.BaseBinaryConvertibles;  // NOTESDescriptor, NOTESVoid, NOTESEventDescriptor, NOTESFieldAnnotation
using EVent.Policy;                                     // FirewallNode, NamespaceNode, NamespaceDirectory, NetworkDirectory
using EVent.NON;                                        // NONSchema, NONType, NONObject, NONValue
using EVent.Scripting;                                  // ScriptingNode, ScriptHost (EVent.Scripting assembly)
```

## Quick start

```csharp
// A type: see references/types.md for the full rules.
public class Reading : IBinaryConvertible
{
    public BinaryConvertibleFloat Celsius { get; set; } = 0f;
    public BinaryConvertibleString Room { get; set; } = "";
    public override string Name => "Reading";                 // part of the type identity
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static Reading()
    {
        d.RegisterField("Celsius", (Reading x) => x.Celsius).Description("degrees Celsius").Range(-40, 125);
        d.RegisterField("Room", (Reading x) => x.Room);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

// Process A: listen on 5698, discoverable as "Home".
var a = new TypeSafeEVentNode("Home", new TCPServer(IPAddress.Any, 5698, "Home"));
await a.HookEventAsync("Home.Reading", (Reading r) => Console.WriteLine(r.Celsius.value), "a room temperature reading");
await a.RegisterFunctionAsync("Home.Count", (NOTESVoid _) => (BinaryConvertibleInt32)42);

// Process B: own listener, find "Home" by multicast discovery, join its network.
var b = new TypeSafeEVentNode("Panel", new TCPServer(IPAddress.Any, 5699, "Panel"));
var joined = await b.TryEstablishConnectionAsync(new Package(ConnectionTypes.TCPDiscovery, "Home"));
if (joined != MergeResult.Connected) throw new Exception($"not joined: {joined}");
bool ok = await b.FireEventAsync("Home.Reading", new Reading { Celsius = 21.5f, Room = "den" });           // waits for subscribers
var counts = await b.CallFunctionAsync<NOTESVoid, BinaryConvertibleInt32>("Home.Count", NOTESVoid.Void);   // one answer per provider
```

Direct TCP instead of discovery: `new Package(ConnectionTypes.TCP, new TCPConnectionData(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 5698)))`.

## Rules that bite

- **Topology is a tree.** Join networks only with `TryEstablishConnection(Async)`: it refuses a link that would close a loop (`MergeResult.AlreadyConnected`), a type conflict (`TypeConflict`) or a non-NOTES peer (`NotNOTES`). Never create cycles with raw links. A loop through two firewall/namespace nodes is not detected.
- **Typed events are synchronous, acknowledged dRPC.** `FireEvent` completes when every subscriber's handler has finished (at most `EVentNode.FunctionCallTimeout`, 30 s by default, or the per-call timeout). One slow handler stalls its publishers up to that timeout.
- **`PublishEvent` is the fire-and-forget form** for streams (sensor readings, telemetry): it completes once the links accepted the event and sends no acknowledgements, so it is several times faster than `FireEvent`, and events from one publisher reach a subscriber in order. It cannot tell you the event was handled (a handler that throws or a link that breaks loses it silently). Use `FireEvent` when the publisher must know.
- **Return values report type problems, not delivery.** `FireEvent` returns `false` only on a type mismatch (`true` includes "nobody subscribed"). `CallFunction` returns `null` only on a type mismatch, an empty collection if no provider. `HookEvent`/`RegisterFunction` return `false` on a type conflict.
- **An ID is either an event or a function**, with one agreed type network-wide. The first proposer's type (and documentation) wins; later users must match the *shape* (field names, leaf types, record `Name`s, caps, optional-ness), not the class. Fields are matched by name, so declaration order doesn't matter.
- **A hook returns once the network routes it** (its announcement is confirmed with one network-wide round trip), so an event fired afterwards reaches it. Beyond a firewall or namespace node it is still eventually consistent: there, wait for propagation (or retry) before relying on delivery.
- **Handlers run on the thread pool**, concurrently and unordered; they may themselves fire and call (even back over the same link). Prefer the `…Async` API from thread-pool code: many blocked pool threads starve the links.
- **Generic order differs**: typed `TypeSafeEVentNode.CallFunction<TIn, TOut>` vs raw `EVentNode.CallFunction<TResult, TInput>`.
- **Don't put a raw subscriber and a typed event on one ID**; the raw one answers typed calls with nothing.
- `TypeSafeEVentNode` starts in its constructor (blocks briefly; `StartupTimeout`, 10 s). `Stop()`/`Dispose()` leaves the network; later calls throw `ObjectDisposedException`.
- Timeouts and TTLs are **process-wide statics** (`EVentNode.FunctionCallTimeout`, `TypeSafeEVentNode.ProposalTimeToLive`, `MergeStepTimeout`, `StartupTimeout`).
- Nothing guarantees delivery order between different sources. Raw events have back-pressure: each queue holds at most `QueuedClient.MaxQueuedDataBytes` (8 MiB) of them, then publishers wait (raw `FireEvent` blocks). Typed events are paced by their acknowledgements.
- Links carry a protocol version in their handshake; a node refuses a peer outside its range with a `LinkFault` saying so (`EVentNode.ProtocolVersion`, `MinimumProtocolVersion`).

## Which piece to use

| Need | Use | Read |
|---|---|---|
| Typed pub/sub or "ask everyone" between programs | `TypeSafeEVentNode` | references/api.md |
| Define the message types | `IBinaryConvertible` + `NOTESDescriptor` | references/types.md |
| Group an app's endpoints under a name; route the group as one entry; use only declared outside names | `NamespaceNode` (+ `imports`) | references/policy-layers.md |
| Restrict what crosses between two networks | `FirewallNode` + `FirewallPolicy` | references/policy-layers.md |
| List what a network offers (types, documentation) | `NetworkDirectory`, `NamespaceDirectory` | references/policy-layers.md |
| Use events without writing classes; define types at run time | NON (`GetEndpointAsync`, `*Dynamic`) | references/non-and-scripting.md |
| Glue two applications with a few lines of C# | `EVent.Scripting` scripting node | references/non-and-scripting.md |
| Raw bytes, tooling, high-rate fire-and-forget | `EVentNode` + `LocalCallbackClient` | references/api.md |
| A C++ program in the network | `evn::Leaf` (`assets/cpp/evn/leaf.hpp`) on a local C# router | references/native.md |
| Tests (in-memory network) | `assets/MemTransport.cs` | references/testing.md |
| A leaf in another language, or debugging bytes | the leaf protocol + the wire format | references/native.md, references/wire-format.md |

## Designing with EVent

- Name IDs with dotted groups (`App.Area.Thing`) so they fit namespaces and prefix routes. Never start an ID or ID part with `$` (reserved for namespace nodes), and don't reuse the NOTES system IDs (`TryInitiateNOTESDescriptor`, `FinalizeNOTESDescriptor`, `AbortNOTESDescriptor`, `QueryDescriptors`, `NOTESQueryDescriptorSet`, `NOTESShareDescriptors`, `StopEvents`, `AllowEvents`, `AwaitReady`, `NOTESMergePing`, `HandleEvent`) or the admin IDs (`EventAdded`, `EventRemoved`, `ListEvents`, `QueryEvents`, `InterconnectRunning`, `CreateInterconnect`, `DisconnectInterconnect`, `LinkFault`).
- Give every endpoint a description (the last argument of `HookEvent`/`RegisterFunction`) and document fields (`.Description/.Range/.Default`): discovery, NON and the scripting node show them, and they are what an AI reads to connect applications.
- Validate incoming values yourself: ranges are documentation, never enforced.
- Keep one type per ID stable: changing a type means a new ID (e.g. `App.Reading.v2`), because the network keeps the agreed one.
