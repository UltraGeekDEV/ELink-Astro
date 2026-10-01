using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Outcome of a command: accepted/sent, not "the hardware finished". Progress arrives as state events.</summary>
public class CommandResult : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Error { get; set; } = "";

    public static CommandResult Success() => new() { Ok = true };
    public static CommandResult Fail(string error) => new() { Ok = false, Error = error };

    public override string Name => "CommandResult";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CommandResult()
    {
        d.RegisterField("Ok", (CommandResult x) => x.Ok);
        d.RegisterField("Error", (CommandResult x) => x.Error).Description("empty on success");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>One piece of equipment as announced on the mesh, whatever backend provides it.</summary>
public class DeviceInfo : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "";
    public BinaryConvertibleString DisplayName { get; set; } = "";
    public BinaryConvertibleString Source { get; set; } = "";

    public override string Name => "DeviceInfo";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DeviceInfo()
    {
        d.RegisterField("Id", (DeviceInfo x) => x.Id).Description("segment used in the device's EVent IDs: ELink.<Kind>.<Id>.*");
        d.RegisterField("Kind", (DeviceInfo x) => x.Kind).Description("Mount | Camera | Focuser | FilterWheel | Rotator | Dome | Weather | Gps");
        d.RegisterField("DisplayName", (DeviceInfo x) => x.DisplayName);
        d.RegisterField("Source", (DeviceInfo x) => x.Source).Description("what provides it, e.g. indi:<server>");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Answer of <see cref="EquipmentIds.List"/>: all devices one provider knows.</summary>
public class DeviceList : IBinaryConvertible
{
    public BinaryConvertibleCollection<DeviceInfo> Devices { get; set; } = new();

    public override string Name => "DeviceList";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DeviceList() { d.RegisterField("Devices", (DeviceList x) => x.Devices); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A device appeared on or left the mesh.</summary>
public class DeviceAnnouncement : IBinaryConvertible
{
    public BinaryConvertibleBool Present { get; set; } = true;
    public DeviceInfo Device { get; set; } = new();

    public override string Name => "DeviceAnnouncement";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DeviceAnnouncement()
    {
        d.RegisterField("Present", (DeviceAnnouncement x) => x.Present);
        d.RegisterField("Device", (DeviceAnnouncement x) => x.Device);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class DeviceKinds
{
    public const string Mount = "Mount", Camera = "Camera", Focuser = "Focuser", FilterWheel = "FilterWheel",
        Rotator = "Rotator", Dome = "Dome", Weather = "Weather", Gps = "Gps";
}

/// <summary>EVent IDs of the equipment layer. Every device kind has one state event and a set of command
/// functions under <c>ELink.&lt;Kind&gt;.&lt;DeviceId&gt;.</c>; the device directory is separate.</summary>
public static class EquipmentIds
{
    /// <summary>Event: <see cref="DeviceAnnouncement"/>, fired when a device appears or leaves.</summary>
    public const string Announce = "ELink.Devices.Announce";
    /// <summary>Function: NOTESVoid in, one <see cref="DeviceList"/> per provider out (dRPC collects every provider's answer).</summary>
    public const string List = "ELink.Devices.List";

    public static string Root(string kind, string id) => $"ELink.{kind}.{id}";
    public static string State(string kind, string id) => Root(kind, id) + ".State";
    public static string Command(string kind, string id, string command) => Root(kind, id) + "." + command;

    /// <summary>EVent IDs allow letters, digits, '_' and '-' here; everything else becomes '_'.</summary>
    public static string Segment(string text) =>
        new(text.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
}
