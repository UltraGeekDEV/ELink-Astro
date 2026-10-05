# C++ leaves (and leaves in other languages)

Non-C# programs join an EVent network as typed **leaf nodes**: one link to a **router** (an ordinary C# `TypeSafeEVentNode`, usually on the same machine, the interconnect between languages and machines), their own NOTES implementation, no routing. A leaf agrees types with the network, knows the agreed types, checks every event and call against them, and answers its own fires and calls locally (loopback). It never forwards and never merges; the router does that.

The C++ library is header-only C++17; the headers are in `assets/cpp/evn/` (`non.hpp`, `dyn.hpp`, `wire.hpp`, `notes.hpp`, `transport.hpp`, `leaf.hpp`). A program includes `<evn/leaf.hpp>` and links the platform's threads (`-pthread`). `non.hpp`, `dyn.hpp`, `wire.hpp` and `notes.hpp` are the portable core: no exceptions, no I/O, no threads (reusable for an embedded implementation). The link is a `Transport` (a reliable byte stream): `TcpTransport` over loopback today (POSIX; a Winsock version under `_WIN32` is written but untested); shared memory is planned behind the same interface. Nothing listens on the C++ side: the leaf dials the router's `TCPServer`.

## The router side (C#)

Nothing special: a typed node with a TCP server the leaf can dial, normally loopback only.

```csharp
var router = new TypeSafeEVentNode("Router", new TCPServer(IPAddress.Loopback, 5698));
// joins other networks as usual: router.TryEstablishConnectionAsync(...)
```

## Types

```cpp
#include <evn/leaf.hpp>

struct Spot { int32_t Floor = 0; std::string Room; };
EVN_RECORD(Spot, Floor, Room)                              // NOTES name "Spot"

struct Thermo {
    float Celsius = 0;
    std::optional<std::string> Label;                     // Label:string?
    std::optional<Spot> Where;                            // Where:Spot?
    std::vector<double> History;                          // History:double[]
    evn::Capped<uint8_t, 16> Flags;                       // Flags:uint8[<=16]
};
EVN_RECORD(Thermo, Celsius, Label, Where, History, Flags)
// a type in a namespace, or another NOTES name: EVN_RECORD_NAMED(app::Thermo, "Thermo", ...) inside that namespace
EVN_DOCS(Thermo, evn::doc("Celsius", "degrees Celsius").range(-40, 125),        // optional field documentation,
                 evn::doc("Label", "sensor label").default_value("unnamed"))     // travels with proposed types
```

An application's own leaf type (like C#'s custom leaves) is a `Codec` specialization:

```cpp
struct Stamp { uint16_t Value = 0; };
template <> struct evn::Codec<Stamp> {
    static std::string ref(evn::Schema& s, int) { return evn::leaf_ref(s, "stamp16"); }
    static bool write(evn::Writer& w, const Stamp& v, int) { w.le(v.Value); return true; }
    static bool read(evn::Reader& r, Stamp& v, int) { return r.le(v.Value); }
};
```

| C++ | NOTES |
|---|---|
| `bool` | `bool` (1 byte, 0/1 strict) |
| `int8_t`/`uint8_t`, `int16_t`/`uint16_t`, `int32_t`/`uint32_t` | `int8`/`uint8`, `int16`/`uint16`, `int32`/`uint32` |
| `int64_t`/`uint64_t` (any integer type maps by size and signedness; plain `char` is refused) | `int64`/`UInt64` |
| `float`, `double` | `float`, `double` |
| `char16_t` | `char16` (C#'s `char`) |
| `evn::Decimal` (`parse`, `to_string`, `to_double`, `from_int64`) | `decimal` |
| `std::string` | `string` |
| `evn::Void` | `Void` |
| `std::vector<T>`, `evn::Capped<T, N>` | `T[]`, `T[<=N]` |
| `std::optional<T>` or `evn::Box<T>` (fields only; `Box` for a record containing itself) | `T?` |

The same shape as a C# class gives the same signature and identical bytes. `evn::signature_of<T>()`, `evn::encode(value, bytes)`, `evn::decode(bytes, value)` (strict) are available for tools and tests. Keep names ASCII (C++ orders names by bytes, .NET by UTF-16 units).

## The leaf

```cpp
evn::LeafOptions options;                                 // call_timeout 30 s, handshake_timeout 5 s, reconnect on, max_workers 64, log
evn::Leaf leaf("sensor", options);
if (!leaf.connect_tcp("127.0.0.1", 5698)) return 1;       // waits for the router's handshake; re-dials a lost link

evn::Token t = leaf.hook<Thermo>("Home.Reading", [](const Thermo& r) { /* ... */ }, "a room temperature reading");
if (!t) { /* type conflict: the network agreed another type */ }
leaf.provide<evn::Void, int32_t>("Home.Count", [](const evn::Void&) { return 42; });
leaf.provide<Spot, std::string>("Home.Name", [](const Spot& s) -> std::optional<std::string> {
    if (s.Room.empty()) return std::nullopt;              // no answer from this provider
    return s.Room;
});

bool ok = leaf.fire("Home.Reading", Thermo{21.5f});       // false only on a type mismatch; true if nobody listens
std::optional<std::vector<int32_t>> n = leaf.call<int32_t>("Home.Count", evn::Void{});  // nullopt: mismatch
leaf.remove(t);
std::optional<evn::wire::Descriptor> d = leaf.type_of("Home.Reading");   // agreed signatures, description, field docs
```

- Same semantics as the C# typed API: first use agrees the type (the proposal carries the description and EVN_DOCS), later users must match; `fire` waits for all subscribers (here and in the network); `call` returns one answer per provider (a call reaches *every* provider).
- `publish(id, v)` (C++) / `await leaf.publish(id, v)` (JS) is fire and forget: no acknowledgements, events from one leaf arrive in order, several times the throughput of `fire` for streams; it cannot tell you the event was handled.
- Async: `fire_async`/`call_async<Out>` return `std::future` (drop it for fire-and-forget); `fire_then`/`call_then<Out>(id, v, done)` call back like a handler. `evn::CallOptions(evn::Millis(2000), cancel)` sets a timeout and an `evn::Cancel` (`cancel.cancel()` completes the call now with what arrived). `provide_async<In, Out>(id, [](const In&, evn::Answer<Out> answer) { ... answer(v); })` answers later from any thread.
- Handlers run on the leaf's handler threads, concurrently (`Dispatch::Threads`, default); they may fire and call. With `options.dispatch = evn::Dispatch::Poll` they run inside `leaf.poll()` on the application's thread (`options.wakeup` is called when work is waiting); prefer the async calls from that thread. A throwing handler gives no answer.
- Raw events: `hook_raw(id, [](const std::vector<uint8_t>&) {...})`, `fire_raw(id, bytes)` (no types, no waiting).
- `leaf.list()`: every endpoint (id, kind "event"/"function"/"untyped", agreed `wire::Descriptor`), like C#'s `NetworkDirectory` (namespaces expanded).
- Dynamic types (`evn::dyn`): `hook_dynamic(id, [](const evn::dyn::Value& v) {...})`, `fire_dynamic`, `call_dynamic`, `provide_dynamic`; pass signature entries to propose a type (`{"=Order", "Order{Item:string,Qty:int32}"}`). `dyn::Value` indexes records (`v["Where"]["Floor"] = 2`), `dyn::to_json`/`dyn::parse_json`; conversions like NON (integers where they fit, never a fraction into an integer). Custom leaves: `dyn::register_leaf(name, step_over)`, held as raw bytes.
- Offline (no link) the leaf still agrees types with itself and serves its own endpoints; on connecting it shares its types with the network (conflicts are logged).
- `connect_tcp` again while re-dialing changes the target. `stop()` (or destruction) leaves; don't call it from a handler.
- An absent optional field reads as `nullopt` in C++ (a default in the docs is documentation only there; C# applies it).

## Writing a leaf in another language

Implement the NON codec and the plain layout (references/wire-format.md), then:

1. Send `InterconnectRunning` with the handshake (int32 version 1, int32 minimum 1, string implementation); refuse a router outside your range (or with an empty payload, version 0) with a `LinkFault`. Answer the router's `InterconnectRunning` and any `QueryEvents` with `ListEvents` (plain collection of strings): the leaf's interest = its endpoint IDs plus the NOTES functions below. The router's `ListEvents` is its interest (what it routes onward, prefix routes `A.*` included); the link is up once it arrived. Keep both sides current with `EventAdded`/`EventRemoved`; after an `EventAdded`, call `AwaitReady` (it travels behind the notice): when it completes, the whole network routes the ID to you.
2. Raw events are `Data` frames: send if the router routes the ID; deliver incoming ones; no answers.
3. Calling ID X: if the router's interest covers X, send a `FunctionCall` (`EVentFunction`: Parameters = frame X/FunctionCall/input, empty ReturnValues, a Handle of your choosing); also run your own handlers of X; the router answers once with a `FunctionReturn` carrying your Handle and all other answers. Typed events are calls whose providers answer nothing.
4. Answer every `FunctionCall` with a `FunctionReturn`: same `EVentFunction` (Handle, Parameters) with one `Package(X, FunctionReturn, answer)` per non-empty answer. Always answer.
5. Answer the NOTES functions like a .NET node:

| ID | in → out | behaviour |
|---|---|---|
| `TryInitiateNOTESDescriptor` | descriptor → int32 | established: 2 if same type else -1; live pending (TTL 5 s): -1 if different type, else replace it and 0 when pending weight < incoming weight, else 1; none: store as pending, 0 |
| `FinalizeNOTESDescriptor` | descriptor → int32 | commit; 1, or -1 if a different type is established |
| `AbortNOTESDescriptor` | descriptor → Void (1 byte) | drop pending only if same type and weight |
| `QueryDescriptors` | string → descriptor | established one, or no answer |
| `NOTESQueryDescriptorSet` | strings → descriptors | the known ones, or no answer |
| `NOTESShareDescriptors` | descriptors → strings | commit each; answer the conflicting IDs, or nothing |
| `StopEvents` / `AllowEvents` | Void → Void | merge freeze: close the gate (new fires/calls/agreements wait), answer once in-flight ones finished / reopen |
| `AwaitReady` | Void → Void | answer once the gate is open |

6. Proposing: `TryInitiate` (weight random 0…2^31-2) everywhere including yourself. All 0: `Finalize`; any -1 there: `Abort`, fail. Otherwise `Abort`; any -1: fail; any 2: `QueryDescriptors`, adopt the equal one, restart; else wait 100 ms, restart.
7. After the handshake, if the router's list has `NOTESShareDescriptors` and you know types, call it with all of them.
8. A malformed frame or protocol payload: send `LinkFault` (string reason) and close.
