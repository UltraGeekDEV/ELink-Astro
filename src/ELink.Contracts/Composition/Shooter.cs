using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Composition;

/// <summary>Something that can take frames: a camera (with its filter wheel), or a smart scope fanning out to several.
/// Commands under ELink.Shooter.&lt;Id&gt;.: Expose(<see cref="ShooterExposure"/>), Abort(Void), GetState(Void); frames on <c>.Shot</c>.</summary>
public class ShooterState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Disconnected";
    public BinaryConvertibleDouble ExposureRemaining { get; set; } = 0.0;
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 ShotsPerExposure { get; set; } = 1;

    public override string Name => "ShooterState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ShooterState()
    {
        d.RegisterField("Phase", (ShooterState x) => x.Phase).Description("Disconnected | Idle | Exposing | Error");
        d.RegisterField("ExposureRemaining", (ShooterState x) => x.ExposureRemaining).Description("seconds");
        d.RegisterField("Filter", (ShooterState x) => x.Filter).Description("current filter name, empty if there is no wheel");
        d.RegisterField("Message", (ShooterState x) => x.Message);
        d.RegisterField("ShotsPerExposure", (ShooterState x) => x.ShotsPerExposure).Description("Shot events one Expose produces: 1 for a camera, the sum over its shooters for a scope");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ShooterExposure : IBinaryConvertible
{
    public BinaryConvertibleDouble Seconds { get; set; } = 1.0;
    public BinaryConvertibleString FrameType { get; set; } = "Light";
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleInt32 BinX { get; set; } = 0;
    public BinaryConvertibleInt32 BinY { get; set; } = 0;
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleDouble Offset { get; set; } = double.NaN;

    public override string Name => "ShooterExposure";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ShooterExposure()
    {
        d.RegisterField("Seconds", (ShooterExposure x) => x.Seconds).Range(0, 86400);
        d.RegisterField("FrameType", (ShooterExposure x) => x.FrameType).Description("Light | Dark | Bias | Flat");
        d.RegisterField("Filter", (ShooterExposure x) => x.Filter).Description("filter name to use first; empty = leave the wheel alone");
        d.RegisterField("BinX", (ShooterExposure x) => x.BinX).Description("0 = leave as is");
        d.RegisterField("BinY", (ShooterExposure x) => x.BinY).Description("0 = leave as is");
        d.RegisterField("Gain", (ShooterExposure x) => x.Gain).Description("NaN = leave as is");
        d.RegisterField("Iso", (ShooterExposure x) => x.Iso).Description("DSLRs: e.g. 800; empty = leave as is");
        d.RegisterField("Offset", (ShooterExposure x) => x.Offset).Description("NaN = leave as is (or the train's preset)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A finished frame with its context.</summary>
public class ShotEvent : IBinaryConvertible
{
    public BinaryConvertibleString Shooter { get; set; } = "";
    public BinaryConvertibleString Format { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleString FrameType { get; set; } = "";
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleString Timestamp { get; set; } = "";
    public BinaryConvertibleDouble PointingRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble PointingDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleString ObjectName { get; set; } = "";
    public BinaryConvertibleString PlanId { get; set; } = "";
    public RawBytes Data { get; set; } = new();
    public BinaryConvertibleString Quality { get; set; } = "";
    public BinaryConvertibleString QualityNote { get; set; } = "";
    public BinaryConvertibleInt32 Stars { get; set; } = -1;
    public BinaryConvertibleDouble Hfr { get; set; } = double.NaN;
    public BinaryConvertibleDouble Elongation { get; set; } = double.NaN;
    public BinaryConvertibleDouble Background { get; set; } = double.NaN;

    public override string Name => "ShotEvent";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ShotEvent()
    {
        d.RegisterField("Shooter", (ShotEvent x) => x.Shooter).Description("id of the leaf shooter that took it");
        d.RegisterField("Format", (ShotEvent x) => x.Format);
        d.RegisterField("ExposureSeconds", (ShotEvent x) => x.ExposureSeconds);
        d.RegisterField("FrameType", (ShotEvent x) => x.FrameType);
        d.RegisterField("Filter", (ShotEvent x) => x.Filter);
        d.RegisterField("Timestamp", (ShotEvent x) => x.Timestamp);
        d.RegisterField("PointingRaHours", (ShotEvent x) => x.PointingRaHours).Description("J2000, NaN if unknown; added by a smart scope that knows where it pointed");
        d.RegisterField("PointingDecDegrees", (ShotEvent x) => x.PointingDecDegrees).Description("J2000, NaN if unknown");
        d.RegisterField("ObjectName", (ShotEvent x) => x.ObjectName).Description("what was being observed, as named by the Observe request; empty for manual exposures");
        d.RegisterField("PlanId", (ShotEvent x) => x.PlanId).Description("sequence plan the frame belongs to, if any");
        d.RegisterField("Data", (ShotEvent x) => x.Data);
        d.RegisterField("Quality", (ShotEvent x) => x.Quality).Description("Good | Rejected | empty (not graded); graded by the scope against its recent good frames");
        d.RegisterField("QualityNote", (ShotEvent x) => x.QualityNote).Description("why it was rejected");
        d.RegisterField("Stars", (ShotEvent x) => x.Stars).Description("stars found; -1 = not measured");
        d.RegisterField("Hfr", (ShotEvent x) => x.Hfr).Description("median half-flux radius, pixels");
        d.RegisterField("Elongation", (ShotEvent x) => x.Elongation).Description("median star elongation, 1 = round");
        d.RegisterField("Background", (ShotEvent x) => x.Background).Description("sky level, ADU");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ShooterIds
{
    public const string Kind = "Shooter";
    public static string State(string id) => EquipmentIds.State(Kind, id);
    public static string GetState(string id) => EquipmentIds.GetState(Kind, id);
    public static string Expose(string id) => EquipmentIds.Command(Kind, id, "Expose");
    public static string Abort(string id) => EquipmentIds.Command(Kind, id, "Abort");
    public static string Shot(string id) => EquipmentIds.Command(Kind, id, "Shot");
}
