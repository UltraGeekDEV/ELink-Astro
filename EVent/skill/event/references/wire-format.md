# EVent wire format (for peers in other languages, and for debugging)

Everything is little-endian except the port inside discovery datagrams. Strings are UTF-8. Nothing carries type tags: the layout follows from the event ID and, for typed events, from the agreed NON signature. Parsers are strict: a frame or payload that doesn't parse exactly makes the node send a `LinkFault` notice and drop the link.

## Frame (one package)

```
int32  length      bytes after this field: 4 + idLen + 1 + dataLen
int32  idLen
byte[] eventID     UTF-8, idLen bytes
byte   type        0 Invalid, 1 Data, 2 BroadcastHandshake, 3 ServerAdminEvent, 4 FunctionCall, 5 FunctionReturn
byte[] data        the rest
```

Example: `Package("Ping", Data, int32 42)` = `0D000000 04000000 50696E67 01 2A000000`. Maximum frame size is 202 MiB. Over TCP, frames are simply concatenated on the socket; there is no connection preamble.

## Primitive encodings (NON leaves)

| NON name | Encoding |
|---|---|
| `bool` | 1 byte, 0 or 1 (anything else is refused) |
| `int8` / `uint8` | 1 byte |
| `int16` / `uint16` | 2 bytes |
| `int32` / `uint32` | 4 bytes |
| `int64` / `UInt64` | 8 bytes |
| `float` / `double` | 4 / 8 bytes IEEE-754 |
| `decimal` | 16 bytes: the four int32 of .NET `decimal.GetBits` (lo, mid, hi, flags: scale in bits 16-23 ≤ 28, sign bit 31, other bits 0) |
| `char16` | 2 bytes, one UTF-16 code unit |
| `string` | int32 byte length + UTF-8 bytes |
| `Void` | 1 byte `0x00` |

## Two layouts for composite values

**Plain layout** (EVent's own structures and raw `Data` events): a record's members with no names or separators; all leaf and collection members first in registration order, then record members in registration order. A collection is an int32 count, then per item an int32 byte length and the item's bytes (length 0 = null). A nested `Package` is a whole frame. An optional member is a presence byte (0/1) then the value.

**NON layout** (typed events and functions): defined by the agreed signature.

| Kind | Encoding |
|---|---|
| Leaf | as above |
| Record | its fields in **ordinal (UTF-16 code unit) order of their names**, each in NON layout, no framing |
| List | int32 element count, then each element in NON layout (no per-element length; every element is at least one byte) |
| Optional field | 1 byte, 0 = absent / 1 = present, then the value if present |

## NON signatures

A signature is a sorted (ordinal) list of strings: exactly one root entry `=TypeRef`, and one `Name{field:TypeRef,...}` per record type with fields sorted by name. `TypeRef` is a leaf name, a record name, `TypeRef[]` (list) or `TypeRef[<=N]` (list capped at N); a field's TypeRef may end with `?` (optional). Names may not contain `{}[]:,=<>|?@`.

```
["=Kennel", "Cat{Age:UInt64,ID:UInt64,Name:string,Owner:string}", "Kennel{Cats:Cat[],Street:string}"]
```

A `Kennel { Street = "Main", Cats = [ Cat Tom/7/3/Ann ] }` in NON layout: `01000000` (1 cat) `0300000000000000` (Age) `0700000000000000` (ID) `03000000 546F6D` (Name) `03000000 416E6E` (Owner) `04000000 4D61696E` (Street).

A peer must sort names and signature entries exactly this way; ASCII names make it a byte-wise comparison.

## The link protocol

Admin packages are `ServerAdminEvent` frames:

| EventID | Payload | Meaning |
|---|---|---|
| `InterconnectRunning` | int32 protocol version, int32 oldest version accepted, string implementation (empty = version 0) | Sent by each side when a link comes up. Currently 1/1. A peer outside the receiver's range gets a `LinkFault` and the link drops; otherwise answer with `ListEvents`. |
| `ListEvents` | plain collection of strings | The IDs the sender wants routed to it (its interest, including prefix routes `X.*`). |
| `QueryEvents` | empty | "Send me your `ListEvents`." |
| `EventAdded` / `EventRemoved` | string ID | Interest gained / lost. |
| `LinkFault` | string reason | Sent just before a link is dropped for malformed input. |

A minimal peer: connect (or accept), send `InterconnectRunning` (with the version), answer the node's `InterconnectRunning` with `ListEvents` of the IDs it handles, and send `EventAdded`/`EventRemoved` as that changes. The node only sends a peer what the peer declared interest in.

- **Raw event**: a `Data` frame, ID = event, data = payload (plain layout, no type checking).
- **Call (dRPC)**: a `FunctionCall` frame, ID = function, data = an `EVentFunction` in plain layout:

  ```
  Package  Parameters     a full frame: same ID, type FunctionCall, data = the input
  plain collection of Package  ReturnValues   empty in a call
  UInt64   FunctionCallID  ┐ the Handle: identifies the call on this hop
  int32    ClientID        ┘
  ```

  `Parameters.EventID` must equal the frame's ID.
- **Answering a call** (the peer provides the function): send a `FunctionReturn` frame with the same ID and the same `EVentFunction` (same Handle, same Parameters) whose `ReturnValues` holds one `Package(ID, FunctionReturn, answerBytes)` per answer, or none. Always answer, even with nothing; the caller waits until every provider answered or the timeout passes.
- **Making a call**: send a `FunctionCall` with a Handle of your own choosing (unique among your pending calls); the node answers with one `FunctionReturn` carrying your Handle and every provider's answers merged in `ReturnValues`.
- **Typed events** are calls whose input is the NON-layout value and whose providers answer with no return values (an acknowledgement). **Typed functions** answer NON-layout values of the agreed return type.

## Types on the wire

The network's agreed types are ordinary functions any peer can call:

- `QueryDescriptors` (input: string ID) → `NOTESEventDescriptor` (plain layout): `Expected` (collection of strings: the input signature), `EventID` (string), `Returns` (collection of strings; `["=Void"]` for an event), `RandomWeight` (int32), `Description` (string), `InputAnnotations`, `ReturnAnnotations` (collections of six strings each: record, field, description, min, max, default JSON). A node that doesn't know the ID gives no answer.
- A peer in another language should be a leaf: references/native.md has the leaf protocol, including how to answer and propose types.

## Discovery

`TCPServer` answers UDP multicast discovery on `239.255.12.85:8563` for its server IDs; discovery is a convenience between .NET nodes. A foreign peer should dial a node's TCP port directly or listen and be dialed (`ConnectionTypes.TCP` with `TCPConnectionData` on the .NET side).
