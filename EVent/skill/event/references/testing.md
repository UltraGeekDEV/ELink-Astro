# Testing code that uses EVent

## An in-memory network

`assets/MemTransport.cs` (next to this skill) is a complete in-memory transport built on EVent's public API. Copy it into the test project; it needs nothing but a reference to EVent. Nodes on it run the real framing, handshake, routing and type agreement.

```csharp
using EVent.Testing;

var a = new TypeSafeEVentNode("A", new MemProtocol("A"));
var b = new TypeSafeEVentNode("B", new MemProtocol("B"));
Assert.True(b.TryEstablishConnection(MemProtocol.To("A")));   // B joins A's network
// ... test ...
a.Stop(); b.Stop();
```

- `MemProtocol` names are process-global: unique names per test, and stop the nodes afterwards.
- Plain `EVentNode`s: `MemLink.Connect(nodeA, nodeB)` links two directly (no loop check).
- Faults: each `MemProtocol.Streams` holds the streams it dialed; on a `MemStream`: `Kill()` (cable pull), `BreakWrites = true` (half-open link), `InjectIncoming(bytes)` (a hostile peer: a malformed frame drops that link with a `LinkFault`), `LatencyMs` (pipelined latency), `SendDelayMs` (a slow link).

Loopback TCP works too (distinct ports per node, `ConnectionTypes.TCP` with `TCPConnectionData`); avoid discovery in CI, where multicast is often not routed.

## Timing

- **Hooks are routed when they return** (within one network; a firewall or namespace node in between makes it eventually consistent again). Across policy layers, or after a raw local-layer `HookEvent`, poll a condition with a deadline rather than sleeping a fixed time.

  ```csharp
  static async Task<bool> Eventually(Func<bool> condition, int ms = 3000)
  {
      var until = DateTime.UtcNow.AddMilliseconds(ms);
      while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(10); }
      return condition();
  }
  ```

- A typed `FireEvent` completes only after every known subscriber's handler ran, so its side effects are visible when it returns.
- Tests that expect silence (a provider that never answers) should lower `EVentNode.FunctionCallTimeout`; it is process-wide, so restore it (or isolate those tests).
- Several nodes in one process are fine; each `TypeSafeEVentNode` constructor takes a few milliseconds.
- Joining networks (`TryEstablishConnection`) briefly pauses typed traffic on the joining side; do setup before the timed part of a test.
