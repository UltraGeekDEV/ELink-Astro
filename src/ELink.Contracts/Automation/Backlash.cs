using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>A mount's backlash, measured by plate solving: the same spot reached moving up and moving down each axis lands
/// apart by this much. Positive = it stops short of where it is sent (the usual kind of slack in the gears).</summary>
public class BacklashMeasurement : IBinaryConvertible
{
    public BinaryConvertibleString MountId { get; set; } = "";
    public BinaryConvertibleString ScopeId { get; set; } = "";
    public BinaryConvertibleDouble RaArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble DecArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble RaSpreadArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble DecSpreadArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble DecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleString MeasuredUtc { get; set; } = "";
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "BacklashMeasurement";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static BacklashMeasurement()
    {
        d.RegisterField("MountId", (BacklashMeasurement x) => x.MountId).Description("the pointer (mount) it belongs to");
        d.RegisterField("ScopeId", (BacklashMeasurement x) => x.ScopeId).Description("the scope it was measured with");
        d.RegisterField("RaArcsec", (BacklashMeasurement x) => x.RaArcsec).Description("RA axis, in arcseconds of the axis' turn (on the sky it is this times cos Dec)");
        d.RegisterField("DecArcsec", (BacklashMeasurement x) => x.DecArcsec).Description("Dec axis, arcseconds");
        d.RegisterField("RaSpreadArcsec", (BacklashMeasurement x) => x.RaSpreadArcsec).Description("how much the repeats differed: the measurement's own uncertainty");
        d.RegisterField("DecSpreadArcsec", (BacklashMeasurement x) => x.DecSpreadArcsec);
        d.RegisterField("DecDegrees", (BacklashMeasurement x) => x.DecDegrees).Description("where it was measured");
        d.RegisterField("MeasuredUtc", (BacklashMeasurement x) => x.MeasuredUtc).Description("ISO 8601");
        d.RegisterField("Ok", (BacklashMeasurement x) => x.Ok);
        d.RegisterField("Message", (BacklashMeasurement x) => x.Message).Description("why it failed, or a note");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class BacklashState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleCollection<BacklashMeasurement> Mounts { get; set; } = new();

    public override string Name => "BacklashState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static BacklashState()
    {
        d.RegisterField("Phase", (BacklashState x) => x.Phase).Description("Idle | Measuring");
        d.RegisterField("Message", (BacklashState x) => x.Message).Description("what it is doing");
        d.RegisterField("Mounts", (BacklashState x) => x.Mounts, maxCount: 32).Description("the latest measurement of each mount");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class BacklashIds
{
    public const string Root = "ELink.Automation.Backlash";
    /// <summary>BinaryConvertibleString (a scope id) in, BacklashMeasurement out: a few moves each way on both axes, plate solved.
    /// The scope ends where it started.</summary>
    public const string Measure = Root + ".Measure";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
