using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Composition;

/// <summary>A camera in an imaging train: it images, or it guides (an off-axis guider behind the same optics).</summary>
public class TrainCamera : IBinaryConvertible
{
    public BinaryConvertibleString CameraId { get; set; } = "";
    public BinaryConvertibleString Role { get; set; } = "Imaging";
    public BinaryConvertibleDouble PixelSizeUm { get; set; } = 0.0;
    public BinaryConvertibleInt32 SensorWidth { get; set; } = 0;
    public BinaryConvertibleInt32 SensorHeight { get; set; } = 0;

    public override string Name => "TrainCamera";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TrainCamera()
    {
        d.RegisterField("CameraId", (TrainCamera x) => x.CameraId);
        d.RegisterField("Role", (TrainCamera x) => x.Role).Description("Imaging | Guiding (e.g. an off-axis guider camera)");
        d.RegisterField("PixelSizeUm", (TrainCamera x) => x.PixelSizeUm).Description("for cameras whose driver does not know its sensor (DSLRs): handed to the camera; 0 = the camera's own");
        d.RegisterField("SensorWidth", (TrainCamera x) => x.SensorWidth).Description("pixels; 0 = the camera's own");
        d.RegisterField("SensorHeight", (TrainCamera x) => x.SensorHeight);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>One optical train: a telescope (or lens), the cameras behind it, and what sits between. It is a Shooter
/// (all its imaging cameras at once) and, with a guiding camera, also offers that camera as the Shooter
/// <c>&lt;id&gt;-guide</c>. A whole train can be a scope's guide scope; a scope may carry any number of trains.</summary>
public class ImagingTrainDefinition : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble FocalLengthMm { get; set; } = 0.0;
    public BinaryConvertibleDouble ApertureMm { get; set; } = 0.0;
    public BinaryConvertibleCollection<TrainCamera> Cameras { get; set; } = new();
    public BinaryConvertibleString FilterWheelId { get; set; } = "";
    public BinaryConvertibleString FocuserId { get; set; } = "";
    public BinaryConvertibleString RotatorId { get; set; } = "";

    public override string Name => "ImagingTrainDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingTrainDefinition()
    {
        d.RegisterField("Id", (ImagingTrainDefinition x) => x.Id).Description("the train id; also its Shooter id");
        d.RegisterField("Label", (ImagingTrainDefinition x) => x.Label).Description("e.g. RedCat 51 + ASI2600MC");
        d.RegisterField("FocalLengthMm", (ImagingTrainDefinition x) => x.FocalLengthMm).Description("effective, with any reducer; 0 = unknown. Handed to the cameras (FITS FOCALLEN, plate scale)");
        d.RegisterField("ApertureMm", (ImagingTrainDefinition x) => x.ApertureMm);
        d.RegisterField("Cameras", (ImagingTrainDefinition x) => x.Cameras, maxCount: 8);
        d.RegisterField("FilterWheelId", (ImagingTrainDefinition x) => x.FilterWheelId).Description("in front of the first imaging camera; empty = none");
        d.RegisterField("FocuserId", (ImagingTrainDefinition x) => x.FocuserId);
        d.RegisterField("RotatorId", (ImagingTrainDefinition x) => x.RotatorId);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class TrainCameraInfo : IBinaryConvertible
{
    public BinaryConvertibleString CameraId { get; set; } = "";
    public BinaryConvertibleString Role { get; set; } = "";
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble FieldWidthDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble FieldHeightDegrees { get; set; } = double.NaN;

    public override string Name => "TrainCameraInfo";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TrainCameraInfo()
    {
        d.RegisterField("CameraId", (TrainCameraInfo x) => x.CameraId);
        d.RegisterField("Role", (TrainCameraInfo x) => x.Role);
        d.RegisterField("ShooterId", (TrainCameraInfo x) => x.ShooterId).Description("the Shooter for this one camera");
        d.RegisterField("Connected", (TrainCameraInfo x) => x.Connected);
        d.RegisterField("PixelScaleArcsec", (TrainCameraInfo x) => x.PixelScaleArcsec).Description("from the focal length and the camera's pixel size and binning");
        d.RegisterField("FieldWidthDegrees", (TrainCameraInfo x) => x.FieldWidthDegrees);
        d.RegisterField("FieldHeightDegrees", (TrainCameraInfo x) => x.FieldHeightDegrees);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class TrainState : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble FocalLengthMm { get; set; } = 0.0;
    public BinaryConvertibleDouble ApertureMm { get; set; } = 0.0;
    public BinaryConvertibleCollection<TrainCameraInfo> Cameras { get; set; } = new();
    public BinaryConvertibleString FilterWheelId { get; set; } = "";
    public BinaryConvertibleString FocuserId { get; set; } = "";
    public BinaryConvertibleString RotatorId { get; set; } = "";
    public BinaryConvertibleString GuideShooterId { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "TrainState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TrainState()
    {
        d.RegisterField("Id", (TrainState x) => x.Id);
        d.RegisterField("Label", (TrainState x) => x.Label);
        d.RegisterField("FocalLengthMm", (TrainState x) => x.FocalLengthMm);
        d.RegisterField("ApertureMm", (TrainState x) => x.ApertureMm);
        d.RegisterField("Cameras", (TrainState x) => x.Cameras, maxCount: 8);
        d.RegisterField("FilterWheelId", (TrainState x) => x.FilterWheelId);
        d.RegisterField("FocuserId", (TrainState x) => x.FocuserId);
        d.RegisterField("RotatorId", (TrainState x) => x.RotatorId);
        d.RegisterField("GuideShooterId", (TrainState x) => x.GuideShooterId).Description("Shooter of its guiding camera; empty = none");
        d.RegisterField("Message", (TrainState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Optics handed to a camera (INDI SCOPE_INFO), so its frames carry the focal length.</summary>
public class CameraOptics : IBinaryConvertible
{
    public BinaryConvertibleDouble FocalLengthMm { get; set; } = 0.0;
    public BinaryConvertibleDouble ApertureMm { get; set; } = 0.0;

    public override string Name => "CameraOptics";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CameraOptics()
    {
        d.RegisterField("FocalLengthMm", (CameraOptics x) => x.FocalLengthMm);
        d.RegisterField("ApertureMm", (CameraOptics x) => x.ApertureMm);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class TrainIds
{
    public const string Kind = "Train";
    public static string State(string id) => EquipmentIds.State(Kind, id);
    public static string GetState(string id) => EquipmentIds.GetState(Kind, id);
    /// <summary>The Shooter id of a train's guiding camera.</summary>
    public static string GuideShooter(string trainId) => trainId + "-guide";
    /// <summary>The Shooter id of one camera of a train.</summary>
    public static string CameraShooter(string trainId, string cameraId) => trainId + "-" + EquipmentIds.Segment(cameraId);
    /// <summary>Camera command taking CameraOptics.</summary>
    public const string CameraSetOptics = "SetOptics";
}
