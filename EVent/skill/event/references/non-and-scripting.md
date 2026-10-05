# NON dynamic objects and the scripting node

## NON (namespace `EVent.NON`): any agreed type without a C# class

The bytes are identical to the typed API's, so dynamic and typed nodes interoperate both ways.

```csharp
var ep = await node.GetEndpointAsync("House.Setpoint");       // agreed type, fetched if needed; null if none
var spot = ep!.NewInputObject();                              // record with zero values, optionals absent
spot["Floor"] = 2;                                            // setters check types; integers convert where they fit, any number to float/double/decimal
spot["Room"] = "attic";
List<NONValue>? answers = await node.CallFunctionDynamicAsync("House.Setpoint", spot);   // null = type mismatch
float c = answers![0]["Celsius"].AsFloat;
string room = answers[0]["Where"]["Room"].AsString;           // reading through a chain works

await node.HookEventDynamicAsync("Reading", v => Console.WriteLine(v["Celsius"].AsFloat));
var r = (await node.GetEndpointAsync("Reading"))!.NewInputObject();
r["Celsius"] = 20.5f;
r.Object("Where")["Room"] = "den";                            // SETTING through a chain needs Object()/List(): NONValue is a struct
await node.FireEventDynamicAsync("Reading", r);
```

- `NONValue`: struct (number inline, string, record, list, leaf, or `NONValue.Null` = absent optional). `AsBool/AsInt8…AsUInt64/AsFloat/AsDouble/AsDecimal/AsChar/AsString/AsObject/AsList`, implicit from every C# basic type, `string`, `NONObject`, `NONList`. `NONType.Primitives` lists the 15 built-in leaves.
- `NONObject`: `obj["F"]`, `obj[slot]` (resolve once: `obj.Type.Slot("F")`), `Has`, `Clear`, `Object(name)`, `List(name)`, `Clone()`, `ToJson()`, `NONObject.FromJson(type, json)` (strict), `AsDynamic()` for C# `dynamic`.
- `NONList`: `IList<NONValue>`, `AddObject()`, `Object(i)`, enforces the cap.
- Schemas: `NONSchema.For<T>()`, `NONSchema.FromSignature(sig, annotations)`, `NONSchema.FromJson(typeDescriptionJson)` (the discovery JSON shape), `schema.Read/TryRead/Write`, `schema.ToJson()`.
- Absent optional fields read as the *agreed* defaults.

**Defining a new type at run time** (proposed if the ID has no type yet; C# classes with the same shape can join it):

```csharp
var order = NONType.Record("Order")
    .Field("Item", NONType.String, f => f.Description("what was ordered"))
    .Field("Qty", NONType.Int32, f => f.Range(1, 99))
    .Optional("Note", NONType.String, f => f.Default("none"))
    .Field("Tags", NONType.ListOf(NONType.String, 8))
    .Optional("Next", NONType.Self)                            // a type containing itself
    .Build();
await node.HookEventDynamicAsync("Orders", v => Handle(v), define: order, description: "orders placed at the counter");
await node.RegisterFunctionDynamicAsync("Price", v => (NONValue)1.5f, defineInput: order, defineOutput: NONType.Float);
```

Custom leaf types need `NONLeaves.Register<MyLeaf>()` on the node that reads them.

## Scripting node (project `EVent.Scripting`, namespace `EVent.Scripting`)

A typed node with a local web page: pick one input endpoint (an event to react to, or a function to answer) and any outputs (events to fire, functions to call); it generates a read-only C# **frame** (a documented class per record type, shared across endpoints, and a `Handle` signature); you write the body; Roslyn compiles it and it is registered as a handler.

```csharp
var scripting = await ScriptingNode.StartAsync("Scripts", port: 5780, storeDirectory: "/var/lib/event/scripts",
    new TCPServer(IPAddress.Any, 5790, "Scripts"));
scripting.Node.TryEstablishConnection(networkInfo);
Console.WriteLine(scripting.Web.PageUrl);          // http://127.0.0.1:5780/?token=...
```

Frame shape for input `Reading` (event of `Thermo`), outputs `Alerts` (event of `Alert`) and `Hvac.Setpoint` (function `Spot` → `Thermo`):

```csharp
public static async Task Handle(Thermo input,
    Func<Alert, Task<bool>> fireAlerts,
    Func<Spot, Task<IReadOnlyList<Thermo>>> callHvac_Setpoint,
    CancellationToken cancellation)
{
    // body: e.g.
    if (input.Celsius <= 28) return;
    var target = await callHvac_Setpoint(input.Where ?? new Spot { Room = "unknown" });
    await fireAlerts(new Alert { Message = $"{input.Where?.Room} is {input.Celsius} °C", Level = 1 });
}
```

- Generated properties: `int/ulong/float/string`, nullable for optional fields, records as their class, lists as `NONListView<T>`, `Void` as `Nothing`. If the input is a function, `Handle` returns `Task<TAnswer>` (null = no answer).
- Same-shaped records across endpoints are one class; name clashes with different shapes become `Name_2`.
- Programmatic use: `new ScriptHost(node, new ScriptStore(dir))`, `ListEndpointsAsync`, `GenerateFrameAsync(input, outputs)`, `CheckAsync` (compile only), `DeployAsync(input, outputs, body, name, replaceId)`, `Scripts`, `Remove(id)`, `StartRestoring()`.
- The page's "Copy for AI" gives a prompt with the task, frame and NON types; compile errors come back with body line numbers.
- With a store folder, scripts persist (`{id}.json`) and are restored on start; ones whose endpoints aren't agreed yet show as "waiting" and start once the node joins its network.
- Security: scripts run with the process's full rights; the page listens on 127.0.0.1 only, needs the per-start token (`X-EVent-Token` header for the API), and rejects foreign Host/Origin. Run the scripting node as its own low-privilege process; put a firewall node in front to limit what scripts can reach.
