using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Automatic centring: whenever the mount finishes a slew to a new target (from ELink, Ekos, a hand controller...),
/// solve with the guide camera, sync, re-slew until the target is within tolerance. A learned guide-to-primary offset puts the
/// primary camera, not the guide scope, on the target.</summary>
public class CenteringConfig : IBinaryConvertible
{
    public BinaryConvertibleBool Enabled { get; set; } = false;
    public BinaryConvertibleString MountId { get; set; } = "";
    public BinaryConvertibleString GuideShooterId { get; set; } = "";
    public BinaryConvertibleString PrimaryShooterId { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 2.0;
    public BinaryConvertibleDouble ToleranceArcmin { get; set; } = 1.0;
    public BinaryConvertibleInt32 MaxIterations { get; set; } = 5;
    public BinaryConvertibleBool UseOffset { get; set; } = true;
    public BinaryConvertibleBool SyncMount { get; set; } = true;

    public override string Name => "CenteringConfig";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CenteringConfig()
    {
        d.RegisterField("Enabled", (CenteringConfig x) => x.Enabled).Description("centre automatically after every slew to a new target");
        d.RegisterField("MountId", (CenteringConfig x) => x.MountId).Description("the Mount to watch, sync and slew");
        d.RegisterField("GuideShooterId", (CenteringConfig x) => x.GuideShooterId).Description("the Shooter whose frames are solved to centre (usually the guide scope)");
        d.RegisterField("PrimaryShooterId", (CenteringConfig x) => x.PrimaryShooterId).Description("the main camera; used to learn the guide-to-primary offset");
        d.RegisterField("ExposureSeconds", (CenteringConfig x) => x.ExposureSeconds);
        d.RegisterField("ToleranceArcmin", (CenteringConfig x) => x.ToleranceArcmin).Description("good enough when within this");
        d.RegisterField("MaxIterations", (CenteringConfig x) => x.MaxIterations).Range(1, 20);
        d.RegisterField("UseOffset", (CenteringConfig x) => x.UseOffset).Description("put the primary camera on the target using the learned offset");
        d.RegisterField("SyncMount", (CenteringConfig x) => x.SyncMount).Description("correct by syncing the mount and re-slewing; otherwise (or when a sync does not help) aim off by the measured error");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class CenteringState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public CenteringConfig Config { get; set; } = new();
    public BinaryConvertibleDouble TargetRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble TargetDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleInt32 Iteration { get; set; } = 0;
    public BinaryConvertibleDouble ErrorArcmin { get; set; } = double.NaN;
    public BinaryConvertibleBool OffsetKnown { get; set; } = false;
    public BinaryConvertibleDouble OffsetEastArcmin { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetNorthArcmin { get; set; } = 0.0;
    public BinaryConvertibleString OffsetPierSide { get; set; } = "";
    public BinaryConvertibleInt32 Centerings { get; set; } = 0;

    public override string Name => "CenteringState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CenteringState()
    {
        d.RegisterField("Phase", (CenteringState x) => x.Phase).Description("Idle | Watching | Solving | Correcting | Centered | Failed | Calibrating");
        d.RegisterField("Message", (CenteringState x) => x.Message);
        d.RegisterField("Config", (CenteringState x) => x.Config);
        d.RegisterField("TargetRaHours", (CenteringState x) => x.TargetRaHours).Description("where the primary camera should point, J2000");
        d.RegisterField("TargetDecDegrees", (CenteringState x) => x.TargetDecDegrees);
        d.RegisterField("Iteration", (CenteringState x) => x.Iteration);
        d.RegisterField("ErrorArcmin", (CenteringState x) => x.ErrorArcmin).Description("last measured distance from the target");
        d.RegisterField("OffsetKnown", (CenteringState x) => x.OffsetKnown);
        d.RegisterField("OffsetEastArcmin", (CenteringState x) => x.OffsetEastArcmin).Description("where the primary looks relative to the guide scope");
        d.RegisterField("OffsetNorthArcmin", (CenteringState x) => x.OffsetNorthArcmin);
        d.RegisterField("OffsetPierSide", (CenteringState x) => x.OffsetPierSide).Description("pier side it was learned on (the offset turns over with a meridian flip)");
        d.RegisterField("Centerings", (CenteringState x) => x.Centerings).Description("successful centrings since start");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class CenteringIds
{
    public const string Root = "ELink.Automation.Centering";
    /// <summary>CenteringConfig in, CommandResult out.</summary>
    public const string Configure = Root + ".Configure";
    /// <summary>SkyTarget in (J2000 where the primary should point; NaN RA = the mount's current target), CommandResult out.</summary>
    public const string CenterNow = Root + ".CenterNow";
    /// <summary>Void in, CommandResult out: solve guide and primary at the current pointing and keep their offset.</summary>
    public const string CalibrateOffset = Root + ".CalibrateOffset";
    public const string ClearOffset = Root + ".ClearOffset";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
