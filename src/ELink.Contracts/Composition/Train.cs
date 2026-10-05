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
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleDouble Offset { get; set; } = double.NaN;
    public BinaryConvertibleDouble CoolTo { get; set; } = double.NaN;
    public BinaryConvertibleDouble CoolDegreesPerMinute { get; set; } = 3.0;
    public BinaryConvertibleDouble WarmTo { get; set; } = 10.0;
    public BinaryConvertibleBool PseudoMono { get; set; } = false;

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
        d.RegisterField("Gain", (TrainCamera x) => x.Gain).Description("preset used when an exposure does not say (e.g. unity gain); NaN = leave the camera's");
        d.RegisterField("Offset", (TrainCamera x) => x.Offset).Description("preset offset; NaN = leave the camera's");
        d.RegisterField("CoolTo", (TrainCamera x) => x.CoolTo).Description("sensor set point in °C: the train cools to it, slowly, once the camera is connected; NaN = no cooling");
        d.RegisterField("CoolDegreesPerMinute", (TrainCamera x) => x.CoolDegreesPerMinute).Description("how fast the set point moves while cooling and warming").Range(0.1m, 30m);
        d.RegisterField("WarmTo", (TrainCamera x) => x.WarmTo).Description("Warm ramps up to this before switching the cooler off");
        d.RegisterField("PseudoMono", (TrainCamera x) => x.PseudoMono).Description("a colour camera used as three pseudo filters: it takes turns to focus its red, green and blue (the train's focus offsets named R, G, B, relative to the autofocus position, which is green); the in-focus channel of each frame goes to the colour stack, the other two (out of focus) to a luminance stack");
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
    public BinaryConvertibleCollection<FilterFocusOffset> FocusOffsets { get; set; } = new();

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
        d.RegisterField("FocusOffsets", (ImagingTrainDefinition x) => x.FocusOffsets, maxCount: 16).Description("focuser steps per filter (relative to each other): changing filter moves the focuser by the difference");
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
    public BinaryConvertibleDouble AngleDegrees { get; set; } = double.NaN;
    public BinaryConvertibleString Cooler { get; set; } = "";
    public BinaryConvertibleDouble Temperature { get; set; } = double.NaN;
    public BinaryConvertibleDouble CoolerSetPoint { get; set; } = double.NaN;

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
        d.RegisterField("AngleDegrees", (TrainCameraInfo x) => x.AngleDegrees).Description("where the camera's image up points on the sky, east of north, learned from its last plate solve; NaN = not known yet");
        d.RegisterField("Cooler", (TrainCameraInfo x) => x.Cooler).Description("empty (no cooling asked) | Waiting (not connected) | Cooling | Cold | Warming | Off | Error");
        d.RegisterField("Temperature", (TrainCameraInfo x) => x.Temperature);
        d.RegisterField("CoolerSetPoint", (TrainCameraInfo x) => x.CoolerSetPoint).Description("where the ramp has got to");
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
    public BinaryConvertibleCollection<FilterFocusOffset> FocusOffsets { get; set; } = new();
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
        d.RegisterField("FocusOffsets", (TrainState x) => x.FocusOffsets, maxCount: 16);
        d.RegisterField("Message", (TrainState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A filter's focus position relative to the others (steps; only the differences matter).</summary>
public class FilterFocusOffset : IBinaryConvertible
{
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleInt32 Steps { get; set; } = 0;

    public override string Name => "FilterFocusOffset";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FilterFocusOffset()
    {
        d.RegisterField("Filter", (FilterFocusOffset x) => x.Filter);
        d.RegisterField("Steps", (FilterFocusOffset x) => x.Steps);
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
    /// <summary>Void in: cool every camera of the train that has a CoolTo (again, after Warm).</summary>
    public static string Cool(string trainId) => EquipmentIds.Command(Kind, trainId, "Cool");
    /// <summary>Void in: ramp the cooled cameras up to their WarmTo, then switch the coolers off.</summary>
    public static string Warm(string trainId) => EquipmentIds.Command(Kind, trainId, "Warm");
    /// <summary>Camera command taking CameraOptics.</summary>
    public const string CameraSetOptics = "SetOptics";
}
