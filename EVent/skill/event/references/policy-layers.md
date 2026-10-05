# Namespaces, firewalls, discovery (namespace `EVent.Policy`)

Both policy layers are two full `TypeSafeEVentNode`s joined by an in-process link that renames (namespace) or filters (firewall) what crosses. Each side joins its own network like any node: dial from it (`side.TryEstablishConnection(info)`) or let others dial its transport.

## NamespaceNode — a network as a named sub-network

```csharp
var ns = new NamespaceNode("PlayerGate", "Player",
    new TCPServer(IPAddress.Loopback, 5710, "PlayerInside"),    // inner: the network whose IDs get the prefix
    new TCPServer(IPAddress.Any, 5711, "PlayerOutside"),        // outer: where they appear as Player.X
    imports: new[] { "World", "Server" });                      // outer names usable inside, unchanged
ns.Outer.TryEstablishConnection(parentNetworkInfo);
// inner apps join "PlayerInside"; they use local names:
innerNode.RegisterFunction("Health", (BinaryConvertibleString who) => (BinaryConvertibleInt32)17);
innerNode.CallFunction<NOTESVoid, BinaryConvertibleInt32>("World.Time", NOTESVoid.Void);   // an import
// outside: parentNode.CallFunction<BinaryConvertibleString, BinaryConvertibleInt32>("Player.Health", "steve");
```

- **Exports**: every inner ID `X` is `ns.X` outside, announced as **one prefix route `ns.*`** (the outer network holds one routing entry per namespace, however many endpoints).
- **Imports**: outer names the inner network may use, spelled as the parent spells them (`World` covers `World.*`, not `WorldEdit`). Announced inside as `World.*`. **Nothing else of the outer network is visible inside.**
- Nest by chaining: a namespace's `Outer` joins the network inside another; `X` → `Player.X` → `Minecraft.Player.X`. Imports are resolved against the *parent* network (import `Server` in both `Minecraft` and `Minecraft.Player` to reach a global `Server.*` from inside Player).
- Types are agreed across the boundary under both names (the type agreement traffic is renamed), so a mismatched hook on either side is refused.
- `$`-prefixed IDs belong to namespace nodes and never cross. Each namespace node answers `ns.$Describe` and `$Namespaces` on its outer side.
- Behind a prefix route the outside doesn't know which IDs exist: a call for an unknown `ns.X` travels in and completes at once with no answers; a typed fire to an ID nobody agreed a type for counts as "nobody interested" (true).
- An import is a shared name: an inner node providing an imported ID becomes one more provider outside.
- Merge control doesn't cross by default (`passMergeControl: false`): don't create loops through policy layers.

## FirewallNode — what may cross

```csharp
var fw = new FirewallNode("Gate",
    new FirewallPolicy(
        iToO: DirectionRule.Whitelist("Temperature", "House.*"),   // from side I to side O
        oToI: DirectionRule.Blacklist("House.Admin.*")),           // from side O to side I
    new TCPServer(IPAddress.Any, 5720, "GateInside"), new TCPServer(IPAddress.Any, 5721, "GateOutside"));
fw.SideI.TryEstablishConnection(insideInfo);
fw.SideO.TryEstablishConnection(outsideInfo);
fw.OnBlocked(b => Console.WriteLine($"blocked {b.Type} {b.EventID} {b.Direction}"));
```

- A rule is an `Allow` and a `Deny` set: an ID passes if not denied and allowed. `*` = everything; `X.*` = everything under `X.`; a denial wins. Factories: `Block`, `AllowAll`, `Whitelist(...)`, `Blacklist(...)`, or `new DirectionRule(allow, deny)`.
- The policy is **fixed at construction**; to change it, `Stop()` and build a new firewall.
- Blocked IDs are invisible: their interest doesn't cross. A call that reaches a blocked ID is answered at once with nothing. Answers to allowed calls always come back.
- Types of events that may cross are agreed across the firewall; blocked ones stay private per side.
- Typical composition: application network → firewall (only the public IDs in) → namespace (`AppAPI.*`) → shared bus.

## Discovery

```csharp
var all = await new NetworkDirectory(node).ListAsync();          // every endpoint of this node's network, namespaces expanded
string json = await new NetworkDirectory(node).ListJsonAsync();
var dir = new NamespaceDirectory(node);
var namespaces = await dir.ListAsync();                          // $Namespaces: the namespaces of this network (siblings from inside one)
var mc = await dir.DescribeAsync("Minecraft");                   // imports, nested namespaces, endpoints with types
var tree = await dir.DescribeTreeAsync("Minecraft");             // recursively
string export = await dir.DescribeAllJsonAsync();                // everything, as one JSON document
var hp = await dir.DescribeEventAsync("Minecraft.Player.Health"); // one endpoint's agreed type by full name
```

`EndpointDescription`: `Id`, `Kind` (`event`/`function`/`untyped`), `Description`, `Input`/`Returns` (signatures), `InputAnnotations`/`ReturnAnnotations`, helpers `InputType`, `ReturnType`, `InputAnnotation(record, field)`, `ToJson()`. Names in results are respelled for the asking node, so they can be used directly.
