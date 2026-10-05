# EVent 0.2.0

A .NET 8 library for an address-free distributed event and RPC mesh with network-wide type agreement. Programs talk through named **events** and **functions**; every node learns who is interested in what, and the network agrees one type per ID the first time it is used.

```
EVent/
  nuget/       EVent and EVent.Scripting NuGet packages (a local feed)
  cpp/         the C++ leaf library: header-only C++17 (include/evn/)
  js/          the JavaScript leaf (evn.js): browsers and Node 22+, no dependencies
  skill/event  the Claude Code skill: usage notes for AI-assisted development
  examples/    a C# router and a C++ leaf that talk to each other
  wiki/        the documentation (open wiki/index.html)
```

## C# (.NET 8)

Add the folder as a package source, then reference the package:

```bash
dotnet nuget add source /path/to/EVent/nuget --name event-local
dotnet add package EVent --version 0.2.0
dotnet add package EVent.Scripting --version 0.2.0     # optional: the scripting node
```

(Or put the two `.nupkg` files on a feed of your own.) A minimal node:

```csharp
var node = new TypeSafeEVentNode("Home", new TCPServer(IPAddress.Loopback, 5698));
await node.HookEventAsync("Home.Reading", (Reading r) => Console.WriteLine(r.Celsius.value));
```

`examples/csharp/Hello` is a complete one, with a NOTES type.

Events come in two kinds. `FireEvent` waits until every subscriber handled the event and says whether they did. `PublishEvent` is fire and forget: it hands the event to the links and returns, no acknowledgement travels, and events from one publisher arrive in order. Use it for streams (readings, telemetry): it moves several times more events per second, and a publisher faster than a link is slowed down rather than buffered without bound.

```csharp
await node.PublishEventAsync("Home.Tick", (BinaryConvertibleInt32)42);   // true once the links took it; says nothing about handling
```

## C++

Header-only, C++17, needs threads. With CMake:

```cmake
add_subdirectory(/path/to/EVent/cpp evn)
target_link_libraries(my_app PRIVATE evn)
```

Or add `cpp/include` to the include path and link `-pthread`. A C++ program is a **leaf**: it links to a C# node (its router) over TCP.

```cpp
#include <evn/leaf.hpp>
struct Reading { float Celsius = 0; std::string Room; };
EVN_RECORD(Reading, Celsius, Room)

evn::Leaf leaf("sensor");
leaf.connect_tcp("127.0.0.1", 5698);
leaf.fire("Home.Reading", Reading{21.5f, "den"});      // waits for every subscriber
leaf.publish("Home.Tick", int32_t{42});              // fire and forget: no acknowledgements, in order
```

## JavaScript

`js/evn.js` is a leaf for browsers and Node 22+ (WebSocket to a node with a `WebSocketServer`): `await leaf.connect('ws://localhost:8081/')`, then `hook`, `provide`, `fire`, `publish`, `call`. See `js/README.md`.

## Try the examples

```bash
cd examples/csharp/Hello && dotnet run          # the router; leave it running
cd examples/cpp && cmake -B build && cmake --build build && ./build/sensor
```

The router prints `den: 21.5 C`, and the C++ program prints the router's `Home.Count` answer.

## The skill (Claude Code)

Gives Claude the rules, API and pitfalls of EVent when it works on a project that uses it:

```bash
cp -r skill/event ~/.claude/skills/event
```

It is self-contained (it carries the C++ headers and the wire format reference).

## Notes

- Links have no authentication or encryption: run routers on loopback or trusted networks.
- Not built yet: serial testing on real boards, the embedded and Java leaves, Unity support. See `wiki/roadmap.html`.
- The C++ leaf is tested on Linux; the Windows socket code is written but has not been compiled.
