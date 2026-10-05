# EVent for JavaScript

`evn.js` is a leaf node for browsers and Node 22+ (the global `WebSocket`; pass `WebSocket` in the options for another).
No dependencies, one file. It speaks the leaf protocol of the [C++ leaf](../cpp) over a WebSocket, to a C# router.

## The router

```csharp
var node = new TypeSafeEVentNode("router", new WebSocketServer("ws://localhost:8081/"));
```

By default the server accepts programs and pages served from a loopback address; name other origins in
`new WebSocketServer(prefix, "https://app.example")`, or `"*"` for every page. (Any web page can open a WebSocket to
`localhost`, which is why the default isn't "everyone".) Two C# nodes can also link over WebSocket:
`node.TryEstablishConnection(WebSocketServer.To("ws://host:8081/"))`.

## The leaf

```js
import { Leaf, type } from './evn.js';

const Reading = type('Reading', { Reading: { Celsius: 'float', Label: 'string?', History: 'double[]' } });

const leaf = new Leaf('sensor');
await leaf.connect('ws://localhost:8081/');

await leaf.hook('Home.Reading', r => console.log(r.Celsius), { type: Reading });          // an event
await leaf.provide('Home.Count', () => 42, { input: type('Void'), output: type('int32') }); // a function

await leaf.fire('Home.Reading', { Celsius: 21.5, History: [] });       // true; false only on a type mismatch
await leaf.publish('Home.Reading', { Celsius: 21.5, History: [] });    // fire and forget: no acknowledgements, in order, several times faster for streams
const counts = await leaf.call('Home.Count', null);                    // [42, ...]: one answer per provider
```

* **Types** are NOTES signatures. `type(root, records)` builds one: `root` is a leaf (`string`, `int32`, `UInt64`, `decimal`,
  `Void`, ...), a record name, or either with `[]` / `[<=N]`; `records` maps record names to `{ field: typeRef }`, a
  trailing `?` marking an optional field. A type is proposed when the network has none; otherwise the agreed one must match
  (a conflict rejects with `EvnError`). Leave `type` out to use whatever the network agreed.
* **Values** are plain JavaScript: records are objects (an absent optional field is simply missing), lists are arrays,
  `Void` is `null`, `char16` a one-character string, `decimal` a string (exact). 64-bit integers come back as numbers when
  they are safe and `BigInt` otherwise; send either.
* `hookRaw(id, cb)` / `fireRaw(id, bytes)` are untyped events. `remove(token)`, `typeOf(id)`, `knownTypes()`, `onLink(fn)`,
  `connected`, `routerHandshake`, `stop()`.
* `await leaf.list()` is the network directory, like C#'s `NetworkDirectory`: every endpoint, sorted by ID, as
  `{ id, kind, type }` (`kind` is `event`, `function` or `untyped` for raw events; `type` has the signatures, the description
  and the fields' documentation). Namespaces (`Lab.*`) are expanded through their `$Describe`, nested ones included.
* A lost link is dialed again (`reconnect`, `reconnectDelay`); the leaf's interest and types go back to the router. A router
  with an incompatible protocol version is refused, told why, and not dialed again.

`Examples/HelloSite/live.html` is a page that does this: `dotnet run --project Examples -- hello-web`, then open
<http://localhost:8080/live.html> in two tabs.

The browser check of `publish()` is hosted by EVent itself: `dotnet run --project EVent.Harness -c Release -- publish-web` serves `js/` through a file endpoint and a web endpoint and is the router the page dials; open <http://localhost:8090/test/publish.html> (the title says PUBLISH OK, and the page lists what it measured).

## .non files

`writeNonFile(records, type, { verbose, name })` and `readNonFile(bytes, type)` read and write the `.non` format of C#'s `NonFile`
(NON, the wire's byte layout, at rest; see the file endpoint). A **known** file is for a reader that has the type: a fingerprint
of it, then the data, so a file written with another version of the type is refused instead of misread. A **verbose** file holds
the type's descriptor, so `readNonFile(bytes)` needs no type at all. A file is a sequence of records, so `nonRecord(value, type)`
can be appended to one.

## Authentication and wss://

A leaf can prove who it is to a router that requires it (off by default on the router; see [`EVent/Auth/README.md`](../EVent/Auth/README.md)):

```js
const leaf = new Leaf('sensor', { credentials: { userId: 'alice', secret } });
await leaf.connect('wss://hub.example:8443/');   // rejects with the router's answer if refused
leaf.authenticated   // true once the router proved it knows the secret too
```

The secret is never sent: the leaf answers a challenge with an HMAC-SHA256 proof (a small pure-JS implementation, so it works in browsers that
are not secure contexts), and requires the router to prove itself in return. A leaf with credentials refuses a router that does not ask,
unless `allowUnauthenticatedRouter: true`. `wss://` is the browser's or Node's own TLS; for a self-signed certificate in Node, set
`NODE_EXTRA_CA_CERTS` to its PEM.

## Keepalive

`new Leaf(name, { keepAlive: { interval: 5000, timeout: 15000 } })` pings a quiet router and drops one that goes silent (then redials if `reconnect` is on). Anything received counts as hearing from the router. `leaf.roundTrip` is the last ping time in ms. Off by default; the leaf always answers a router's pings. See `EVent/CoreFunctionality/KEEPALIVE.md`.

## Tests

```bash
node --test js/test/codec.test.mjs                           # codec, wire, .non files, SHA-256/HMAC and an offline leaf
dotnet run --project EVent.Harness -- E04 E05 E06 E07 E08    # C# routers with JS leaves over WebSocket (needs node)
```
