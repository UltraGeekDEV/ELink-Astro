using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>An equipment profile: the INDI drivers of a rig, run by ELink's own indiserver.</summary>
public class EquipmentProfile : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleCollection<BinaryConvertibleString> Drivers { get; set; } = new();
    public BinaryConvertibleInt32 Port { get; set; } = 7624;
    public BinaryConvertibleBool AutoConnect { get; set; } = true;

    public override string Name => "EquipmentProfile";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static EquipmentProfile()
    {
        d.RegisterField("Label", (EquipmentProfile x) => x.Label);
        d.RegisterField("Drivers", (EquipmentProfile x) => x.Drivers, maxCount: 64).Description("driver executables, e.g. indi_eqmod_telescope, indi_asi_ccd");
        d.RegisterField("Port", (EquipmentProfile x) => x.Port);
        d.RegisterField("AutoConnect", (EquipmentProfile x) => x.AutoConnect).Description("connect every device once its driver is up");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class EquipmentProfiles : IBinaryConvertible
{
    public BinaryConvertibleCollection<EquipmentProfile> Profiles { get; set; } = new();

    public override string Name => "EquipmentProfiles";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static EquipmentProfiles() { d.RegisterField("Profiles", (EquipmentProfiles x) => x.Profiles, maxCount: 64); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A driver INDI has installed (from /usr/share/indi/*.xml).</summary>
public class DriverEntry : IBinaryConvertible
{
    public BinaryConvertibleString Group { get; set; } = "";
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Manufacturer { get; set; } = "";
    public BinaryConvertibleString Executable { get; set; } = "";

    public override string Name => "DriverEntry";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DriverEntry()
    {
        d.RegisterField("Group", (DriverEntry x) => x.Group).Description("Telescopes, CCDs, Focusers, ...");
        d.RegisterField("Device", (DriverEntry x) => x.Device);
        d.RegisterField("Manufacturer", (DriverEntry x) => x.Manufacturer);
        d.RegisterField("Executable", (DriverEntry x) => x.Executable);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class DriverCatalog : IBinaryConvertible
{
    public BinaryConvertibleCollection<DriverEntry> Drivers { get; set; } = new();

    public override string Name => "DriverCatalog";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DriverCatalog() { d.RegisterField("Drivers", (DriverCatalog x) => x.Drivers, maxCount: 2000); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ProfileState : IBinaryConvertible
{
    public BinaryConvertibleString Running { get; set; } = "";
    public BinaryConvertibleString Phase { get; set; } = "Stopped";
    public BinaryConvertibleInt32 Port { get; set; } = 0;
    public BinaryConvertibleCollection<BinaryConvertibleString> Drivers { get; set; } = new();
    public BinaryConvertibleInt32 Restarts { get; set; } = 0;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "ProfileState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ProfileState()
    {
        d.RegisterField("Running", (ProfileState x) => x.Running).Description("the profile running, empty = none");
        d.RegisterField("Phase", (ProfileState x) => x.Phase).Description("Stopped | Starting | Running | Restarting | Error");
        d.RegisterField("Port", (ProfileState x) => x.Port);
        d.RegisterField("Drivers", (ProfileState x) => x.Drivers, maxCount: 64);
        d.RegisterField("Restarts", (ProfileState x) => x.Restarts).Description("times the indiserver had to be started again");
        d.RegisterField("Message", (ProfileState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ProfileIds
{
    public const string Root = "ELink.Profiles";
    /// <summary>Void in, EquipmentProfiles out.</summary>
    public const string List = Root + ".List";
    /// <summary>EquipmentProfile in, CommandResult out: create or replace by label.</summary>
    public const string Save = Root + ".Save";
    /// <summary>BinaryConvertibleString (label) in.</summary>
    public const string Delete = Root + ".Delete";
    /// <summary>BinaryConvertibleString (label) in: start its indiserver and drivers (stops another running one).</summary>
    public const string Start = Root + ".Start";
    public const string Stop = Root + ".Stop";
    /// <summary>Void in, DriverCatalog out.</summary>
    public const string Catalog = Root + ".Catalog";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
