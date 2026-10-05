// A router: an EVent node other programs (C#, C++ leaves) can join over loopback TCP.
// Run it, then run the C++ example (examples/cpp) against port 5698.
using System.Net;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.TCP;

var node = new TypeSafeEVentNode("Router", new TCPServer(IPAddress.Loopback, 5698));

// Subscribe to a typed event; the first user of an ID defines its type for the whole network.
await node.HookEventAsync("Home.Reading", (Reading r) => Console.WriteLine($"{r.Room.Text}: {r.Celsius.value} C"),
                          "a room temperature reading");

// A stream: PublishEvent is fire and forget (no acknowledgements), so many of these cost little; they arrive in order.
long ticks = 0;
await node.HookEventAsync("Home.Tick", (BinaryConvertibleInt32 t) =>
{
    if (Interlocked.Increment(ref ticks) % 1000 == 0) Console.WriteLine($"{ticks} ticks, the last one {t.Value}");
}, "a counter published in bulk");

// Provide a function; a call reaches every provider and returns one answer from each.
await node.RegisterFunctionAsync("Home.Count", (NOTESVoid _) => (BinaryConvertibleInt32)42, "how many sensors");

Console.WriteLine("Router listening on 127.0.0.1:5698. Press Enter to stop.");
Console.ReadLine();
node.Stop();

// A NOTES type: fields registered once; field order doesn't matter, names and types do.
public class Reading : IBinaryConvertible
{
    public BinaryConvertibleFloat Celsius { get; set; } = 0f;
    public BinaryConvertibleString Room { get; set; } = "";
    public override string Name => "Reading";
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
