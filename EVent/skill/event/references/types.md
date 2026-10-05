# Writing NOTES types

Every payload is an `IBinaryConvertible` (an abstract class despite the `I`): `Name`, `Descriptor`, `FromBytes(ref Span<byte>)`, `ToBytes()`. A **record** lists its fields in a static `NOTESDescriptor`; NOTES derives the canonical type (the NON signature) and the wire layout from that.

## Record template

```csharp
public class Order : IBinaryConvertible
{
    public BinaryConvertibleString Item { get; set; } = "";          // required fields must never be null
    public BinaryConvertibleInt32 Qty { get; set; } = 0;
    public BinaryConvertibleString? Note { get; set; }               // optional: may be null
    public Address? ShipTo { get; set; }                              // optional nested record
    public BinaryConvertibleCollection<Line> Lines { get; set; } = new();   // list of records

    public override string Name => "Order";                            // the record's NON name: part of type identity
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static Order()
    {
        d.RegisterField("Item", (Order x) => x.Item).Description("what was ordered");
        d.RegisterField("Qty", (Order x) => x.Qty).Description("how many").Range(1, 99);
        d.RegisterOptionalField("Note", (Order x) => x.Note, (x, v) => x.Note = v).Default("none");
        d.RegisterOptionalField("ShipTo", (Order x) => x.ShipTo, (x, v) => x.ShipTo = v);
        d.RegisterField("Lines", (Order x) => x.Lines, maxCount: 16);    // capped list: Line[<=16]
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
```

Always this boilerplate: a `static readonly NOTESDescriptor`, fields registered in the static constructor, `FromBytes`/`ToBytes` delegating to it. Initialise required fields (a null required member makes sending fail).

## Built-in leaves

| C# type | NON name | value member | implicit from |
|---|---|---|---|
| `BinaryConvertibleBool` | `bool` | `.Value` | `bool` |
| `BinaryConvertibleSByte` / `BinaryConvertibleByte` | `int8` / `uint8` | `.Value` | `sbyte` / `byte` |
| `BinaryConvertibleInt16` / `BinaryConvertibleUInt16` | `int16` / `uint16` | `.Value` | `short` / `ushort` |
| `BinaryConvertibleInt32` / `BinaryConvertibleUInt32` | `int32` / `uint32` | `.Value` | `int` / `uint` |
| `BinaryConvertibleInt64` / `BinaryConvertibleUInt64` | `int64` / `UInt64` | `.Value` | `long` / `ulong` |
| `BinaryConvertibleFloat` | `float` | `.value` (lower case) | `float` |
| `BinaryConvertibleDouble` | `double` | `.Value` | `double` |
| `BinaryConvertibleDecimal` | `decimal` | `.Value` | `decimal` |
| `BinaryConvertibleChar` | `char16` | `.Value` | `char` |
| `BinaryConvertibleString` | `string` | `.Text` | `string` |
| `NOTESVoid` | `Void` | `NOTESVoid.Void` | – (use for "no input" or "no answer") |
| `BinaryConvertibleCollection<T>` | `T[]` / `T[<=N]` | an `IList<T>` | `T[]` / `IEnumerable<T>` ctor |

A leaf, collection or `NOTESVoid` can itself be an event's type: `HookEvent("Temp", (BinaryConvertibleFloat t) => ...)`.

## Registration API

- `RegisterField(name, getter)`: required member. `RegisterField(name, getter, maxCount)`: required *collection* with a cap (only compiles for collections).
- `RegisterOptionalField(name, getter, setter)` (+ `maxCount` overload): may be null; sent as a presence byte. A setter is required (reading an absent value sets null or the default).
- Each returns a builder: `.Description(text)`, `.Range(min, max)` (decimals; for strings and lists it means length; refused on records and when min > max), `.Default(value)` (typed as the field). **None of this is part of the type or enforced**, except: an optional field that arrives absent reads as a copy of the *receiving* type's default instead of null.
- A reusable capped collection: `class Row : BinaryConvertibleCollection<BinaryConvertibleInt32> { static readonly NOTESDescriptor d = NOTESDescriptor.Collection(maxCount: 4); public override NOTESDescriptor Descriptor => d; }`. If a field cap and the definition cap are both given they must be equal.

## What makes two types "the same"

- The signature: `["=Order", "Line{...}", "Order{Item:string,Lines:Line[<=16],Note:string?,Qty:int32,ShipTo:Address?}", ...]`. Fields are sorted by name, so **declaration order doesn't matter** and different programs can use different classes.
- Identity is the root record's `Name` plus the structure below it; nested records are referenced by `Name`. Renaming a field, changing a leaf type, a cap, or optional-ness is a different type (a conflict for an agreed ID).
- Two *different* record types with the same `Name` in one type graph are an error.
- Names must not contain `{}[]:,=<>|?@` or control characters.
- A type may contain itself only through an optional field or a collection (`Node{Next:Node?}`, `Tree{Children:Tree[]}`); a cycle of required fields is rejected.

## Custom leaf types

A class with an empty descriptor (`new NOTESDescriptor()`, no fields) whose `ToBytes`/`FromBytes` define its own self-delimiting bytes; `Name` is its NON name. Every value should take at least one byte (list elements must). NON dynamic objects and the scripting node can only handle a custom leaf when the C# class is registered: `NONLeaves.Register<MyLeaf>()`.

## Inspecting a type

- `NOTESFlexibleSerDes.For(new Order()).Signature` — the canonical signature; `.Annotations` — the documentation.
- `new Order().Descriptor.ToString()` — the structure as JSON; `Descriptor.ToString(value)` — a value.
- `NOTESSignatureJson.ToJson(signature, annotations)` — the JSON type description from a signature alone (what discovery shows).
