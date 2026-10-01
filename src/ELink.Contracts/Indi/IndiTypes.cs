using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Indi;

// Types of the generic INDI mirror: every INDI property of every device is published, whatever the driver is.
// Values stay INDI strings (numbers are decimal) so nothing is lost; typed device contracts sit on top of this.

/// <summary>One member of an INDI property vector.</summary>
public class IndiElementInfo : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Value { get; set; } = "";
    public BinaryConvertibleString Format { get; set; } = "";
    public BinaryConvertibleDouble Min { get; set; } = 0.0;
    public BinaryConvertibleDouble Max { get; set; } = 0.0;
    public BinaryConvertibleDouble Step { get; set; } = 0.0;

    public override string Name => "IndiElement";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiElementInfo()
    {
        d.RegisterField("Id", (IndiElementInfo x) => x.Id).Description("INDI element name");
        d.RegisterField("Label", (IndiElementInfo x) => x.Label).Description("human label");
        d.RegisterField("Value", (IndiElementInfo x) => x.Value).Description("value as INDI text: decimal number, On/Off, text, light state; for BLOB elements the size in bytes");
        d.RegisterField("Format", (IndiElementInfo x) => x.Format).Description("printf-style display format of numbers (%m = sexagesimal), else empty");
        d.RegisterField("Min", (IndiElementInfo x) => x.Min).Description("number minimum");
        d.RegisterField("Max", (IndiElementInfo x) => x.Max).Description("number maximum");
        d.RegisterField("Step", (IndiElementInfo x) => x.Step).Description("number step");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A property vector: a group of related values of one device, with a state.</summary>
public class IndiPropertyInfo : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Property { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Group { get; set; } = "";
    public BinaryConvertibleString Type { get; set; } = "";
    public BinaryConvertibleString State { get; set; } = "";
    public BinaryConvertibleString Permission { get; set; } = "";
    public BinaryConvertibleString Rule { get; set; } = "";
    public BinaryConvertibleDouble Timeout { get; set; } = 0.0;
    public BinaryConvertibleCollection<IndiElementInfo> Elements { get; set; } = new();

    public override string Name => "IndiProperty";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiPropertyInfo()
    {
        d.RegisterField("Device", (IndiPropertyInfo x) => x.Device).Description("INDI device name");
        d.RegisterField("Property", (IndiPropertyInfo x) => x.Property).Description("INDI property name, e.g. EQUATORIAL_EOD_COORD");
        d.RegisterField("Label", (IndiPropertyInfo x) => x.Label);
        d.RegisterField("Group", (IndiPropertyInfo x) => x.Group);
        d.RegisterField("Type", (IndiPropertyInfo x) => x.Type).Description("Text | Number | Switch | Light | Blob");
        d.RegisterField("State", (IndiPropertyInfo x) => x.State).Description("Idle | Ok | Busy | Alert");
        d.RegisterField("Permission", (IndiPropertyInfo x) => x.Permission).Description("ro | wo | rw");
        d.RegisterField("Rule", (IndiPropertyInfo x) => x.Rule).Description("switch rule: OneOfMany | AtMostOne | AnyOfMany, else empty");
        d.RegisterField("Timeout", (IndiPropertyInfo x) => x.Timeout).Description("seconds the device needs at most to act on a change");
        d.RegisterField("Elements", (IndiPropertyInfo x) => x.Elements, maxCount: 1024);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A property appeared, changed, or went away.</summary>
public class IndiPropertyEvent : IBinaryConvertible
{
    public BinaryConvertibleString Kind { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public IndiPropertyInfo Info { get; set; } = new();

    public override string Name => "IndiPropertyEvent";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiPropertyEvent()
    {
        d.RegisterField("Kind", (IndiPropertyEvent x) => x.Kind).Description("Defined | Updated | Deleted (Deleted with empty Property = the whole device)");
        d.RegisterField("Message", (IndiPropertyEvent x) => x.Message).Description("message attached by the device, often empty");
        d.RegisterField("Info", (IndiPropertyEvent x) => x.Info);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A free-text log line from a device or from the INDI server.</summary>
public class IndiLogEvent : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Text { get; set; } = "";
    public BinaryConvertibleString Timestamp { get; set; } = "";

    public override string Name => "IndiLog";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiLogEvent()
    {
        d.RegisterField("Device", (IndiLogEvent x) => x.Device).Description("empty for the server itself");
        d.RegisterField("Text", (IndiLogEvent x) => x.Text);
        d.RegisterField("Timestamp", (IndiLogEvent x) => x.Timestamp).Description("ISO 8601 UTC, may be empty");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Everything the bridge currently knows, for a late-joining consumer.</summary>
public class IndiSnapshot : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleString Server { get; set; } = "";
    public BinaryConvertibleCollection<IndiPropertyInfo> Properties { get; set; } = new();

    public override string Name => "IndiSnapshot";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiSnapshot()
    {
        d.RegisterField("Connected", (IndiSnapshot x) => x.Connected).Description("whether the bridge is connected to its INDI server");
        d.RegisterField("Server", (IndiSnapshot x) => x.Server);
        d.RegisterField("Properties", (IndiSnapshot x) => x.Properties);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class IndiSetElement : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Value { get; set; } = "";

    public override string Name => "IndiSetElement";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiSetElement()
    {
        d.RegisterField("Id", (IndiSetElement x) => x.Id).Description("element name");
        d.RegisterField("Value", (IndiSetElement x) => x.Value).Description("number as decimal or sexagesimal, switch as On/Off, or text");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Ask the device to change a property (newXXXVector).</summary>
public class IndiSetRequest : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Property { get; set; } = "";
    public BinaryConvertibleCollection<IndiSetElement> Elements { get; set; } = new();

    public override string Name => "IndiSetRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiSetRequest()
    {
        d.RegisterField("Device", (IndiSetRequest x) => x.Device);
        d.RegisterField("Property", (IndiSetRequest x) => x.Property);
        d.RegisterField("Elements", (IndiSetRequest x) => x.Elements, maxCount: 1024);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Outcome of a command: the request was accepted/sent, not that the device finished.</summary>
public class IndiResult : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Error { get; set; } = "";

    public static IndiResult Success() => new() { Ok = true };
    public static IndiResult Fail(string error) => new() { Ok = false, Error = error };

    public override string Name => "IndiResult";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiResult()
    {
        d.RegisterField("Ok", (IndiResult x) => x.Ok);
        d.RegisterField("Error", (IndiResult x) => x.Error).Description("empty on success");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Ask the bridge to receive (or stop receiving) BLOBs of a device.</summary>
public class IndiBlobRequest : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleBool Enabled { get; set; } = true;

    public override string Name => "IndiBlobRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiBlobRequest()
    {
        d.RegisterField("Device", (IndiBlobRequest x) => x.Device);
        d.RegisterField("Enabled", (IndiBlobRequest x) => x.Enabled);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A BLOB (typically a camera frame) delivered by a device.</summary>
public class IndiBlobEvent : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Property { get; set; } = "";
    public BinaryConvertibleString Element { get; set; } = "";
    public BinaryConvertibleString Format { get; set; } = "";
    public RawBytes Data { get; set; } = new();

    public override string Name => "IndiBlob";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static IndiBlobEvent()
    {
        d.RegisterField("Device", (IndiBlobEvent x) => x.Device);
        d.RegisterField("Property", (IndiBlobEvent x) => x.Property);
        d.RegisterField("Element", (IndiBlobEvent x) => x.Element);
        d.RegisterField("Format", (IndiBlobEvent x) => x.Format).Description("file format as the driver reports it, e.g. .fits, .fits.z, .jpg");
        d.RegisterField("Data", (IndiBlobEvent x) => x.Data).Description("the payload exactly as received (compressed if Format ends with .z)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Event and function IDs of the generic INDI mirror, per bridge (server) name.</summary>
public static class IndiIds
{
    /// <summary>EVent IDs may not contain a few special characters; spaces and dots are kept out of the segments.</summary>
    public static string Segment(string text)
    {
        var chars = text.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return new string(chars);
    }

    public static string Root(string server) => "Indi." + Segment(server);
    public static string Property(string server) => Root(server) + ".Property";     // event  IndiPropertyEvent
    public static string Log(string server) => Root(server) + ".Log";               // event  IndiLogEvent
    public static string Blob(string server) => Root(server) + ".Blob";             // event  IndiBlobEvent
    public static string Snapshot(string server) => Root(server) + ".Snapshot";     // func   NOTESVoid -> IndiSnapshot
    public static string Set(string server) => Root(server) + ".Set";               // func   IndiSetRequest -> IndiResult
    public static string EnableBlob(string server) => Root(server) + ".EnableBlob"; // func   IndiBlobRequest -> IndiResult
}
