using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Start or stop saving the frames of a Shooter.</summary>
public class StorageWatch : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleBool Enabled { get; set; } = true;

    public override string Name => "StorageWatch";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static StorageWatch()
    {
        d.RegisterField("ShooterId", (StorageWatch x) => x.ShooterId).Description("a Shooter id: its Shot events are saved");
        d.RegisterField("Enabled", (StorageWatch x) => x.Enabled);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class StorageState : IBinaryConvertible
{
    public BinaryConvertibleString Directory { get; set; } = "";
    public BinaryConvertibleCollection<BinaryConvertibleString> Watching { get; set; } = new();
    public BinaryConvertibleInt32 FramesSaved { get; set; } = 0;
    public BinaryConvertibleString LastFile { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "StorageState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static StorageState()
    {
        d.RegisterField("Directory", (StorageState x) => x.Directory).Description("frames are saved under this directory, in a folder per night");
        d.RegisterField("Watching", (StorageState x) => x.Watching, maxCount: 256).Description("shooter ids being saved");
        d.RegisterField("FramesSaved", (StorageState x) => x.FramesSaved).Description("since the service started");
        d.RegisterField("LastFile", (StorageState x) => x.LastFile);
        d.RegisterField("Message", (StorageState x) => x.Message).Description("last problem, empty if none");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Fired for every frame written to disk.</summary>
public class FrameSaved : IBinaryConvertible
{
    public BinaryConvertibleString Path { get; set; } = "";
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString ObjectName { get; set; } = "";
    public BinaryConvertibleString FrameType { get; set; } = "";
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleInt32 Bytes { get; set; } = 0;

    public override string Name => "FrameSaved";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FrameSaved()
    {
        d.RegisterField("Path", (FrameSaved x) => x.Path);
        d.RegisterField("ShooterId", (FrameSaved x) => x.ShooterId);
        d.RegisterField("ObjectName", (FrameSaved x) => x.ObjectName);
        d.RegisterField("FrameType", (FrameSaved x) => x.FrameType);
        d.RegisterField("Filter", (FrameSaved x) => x.Filter);
        d.RegisterField("ExposureSeconds", (FrameSaved x) => x.ExposureSeconds);
        d.RegisterField("Bytes", (FrameSaved x) => x.Bytes);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class StorageIds
{
    public const string Root = "ELink.Automation.Storage";
    /// <summary>BinaryConvertibleString (a directory) in, CommandResult out.</summary>
    public const string SetDirectory = Root + ".SetDirectory";
    /// <summary>StorageWatch in, CommandResult out.</summary>
    public const string Watch = Root + ".Watch";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
    /// <summary>Event: <see cref="FrameSaved"/>.</summary>
    public const string Saved = Root + ".Saved";
}
