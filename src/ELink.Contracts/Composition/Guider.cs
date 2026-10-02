using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Composition;

// ---- guide outputs: what a guider drives ---------------------------------------------------------------------

/// <summary>One timed guide pulse (ST4 / INDI TELESCOPE_TIMED_GUIDE_NS/WE).</summary>
public class GuidePulse : IBinaryConvertible
{
    public BinaryConvertibleString Direction { get; set; } = "North";
    public BinaryConvertibleInt32 Milliseconds { get; set; } = 0;

    public override string Name => "GuidePulse";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuidePulse()
    {
        d.RegisterField("Direction", (GuidePulse x) => x.Direction).Description("North | South | East | West");
        d.RegisterField("Milliseconds", (GuidePulse x) => x.Milliseconds).Range(0, 60000);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class GuidePortState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleBool PulsingNorthSouth { get; set; } = false;
    public BinaryConvertibleBool PulsingEastWest { get; set; } = false;

    public override string Name => "GuidePortState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuidePortState()
    {
        d.RegisterField("Connected", (GuidePortState x) => x.Connected);
        d.RegisterField("PulsingNorthSouth", (GuidePortState x) => x.PulsingNorthSouth);
        d.RegisterField("PulsingEastWest", (GuidePortState x) => x.PulsingEastWest);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>"You are here, you should be here": a guider's measurement handed to a device that closes the loop itself
/// (an ELink-native mount with encoders, a mount that takes goto-style corrections, an adaptive optics unit, ...).
/// Positions are J2000 and NaN when the guider does not know them absolutely; the offset is always there.</summary>
public class GuideCorrection : IBinaryConvertible
{
    public BinaryConvertibleString GuiderId { get; set; } = "";
    public BinaryConvertibleInt32 Frame { get; set; } = 0;
    public BinaryConvertibleString Utc { get; set; } = "";
    public BinaryConvertibleDouble IsRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble IsDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble ShouldRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble ShouldDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble MoveEastArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble MoveNorthArcsec { get; set; } = 0.0;
    public BinaryConvertibleBool Settling { get; set; } = false;

    public override string Name => "GuideCorrection";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuideCorrection()
    {
        d.RegisterField("GuiderId", (GuideCorrection x) => x.GuiderId);
        d.RegisterField("Frame", (GuideCorrection x) => x.Frame).Description("guide frame number, for ordering");
        d.RegisterField("Utc", (GuideCorrection x) => x.Utc).Description("mid-exposure time of the guide frame, ISO 8601");
        d.RegisterField("IsRaHours", (GuideCorrection x) => x.IsRaHours).Description("where the guided axis points now, J2000; NaN if only the offset is known");
        d.RegisterField("IsDecDegrees", (GuideCorrection x) => x.IsDecDegrees);
        d.RegisterField("ShouldRaHours", (GuideCorrection x) => x.ShouldRaHours).Description("where it should point (the lock, plus any dither)");
        d.RegisterField("ShouldDecDegrees", (GuideCorrection x) => x.ShouldDecDegrees);
        d.RegisterField("MoveEastArcsec", (GuideCorrection x) => x.MoveEastArcsec).Description("the pointing must move this far east on the sky (negative: west) to be where it should");
        d.RegisterField("MoveNorthArcsec", (GuideCorrection x) => x.MoveNorthArcsec).Description("and this far north (negative: south)");
        d.RegisterField("Settling", (GuideCorrection x) => x.Settling).Description("right after a dither or start: a big step is expected");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>IDs of devices that take corrections ("goto guiding") instead of pulses.</summary>
public static class GuideTargetIds
{
    public const string Kind = "GuideTarget";
    /// <summary>Function: GuideCorrection in, CommandResult out. Implemented by the device, called by a guider in Correction mode.</summary>
    public static string Correct(string id) => EquipmentIds.Command(Kind, id, "Correct");
}

// ---- the guider ----------------------------------------------------------------------------------------------

public class GuiderDefinition : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString Output { get; set; } = "Pulse";
    public BinaryConvertibleString GuidePortId { get; set; } = "";
    public BinaryConvertibleString TargetId { get; set; } = "";
    public BinaryConvertibleString MountId { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 2.0;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble CameraAngleDegrees { get; set; } = double.NaN;
    public BinaryConvertibleBool SolveOrientation { get; set; } = true;
    public BinaryConvertibleDouble RaAggressiveness { get; set; } = 0.7;
    public BinaryConvertibleDouble DecAggressiveness { get; set; } = 0.7;
    public BinaryConvertibleDouble MinMovePixels { get; set; } = 0.15;
    public BinaryConvertibleInt32 MaxPulseMs { get; set; } = 2500;
    public BinaryConvertibleInt32 CalibrationStepMs { get; set; } = 1000;
    public BinaryConvertibleDouble CalibrationPixels { get; set; } = 20.0;
    public BinaryConvertibleString DecMode { get; set; } = "Auto";

    public override string Name => "GuiderDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuiderDefinition()
    {
        d.RegisterField("Id", (GuiderDefinition x) => x.Id).Description("the new Guider id");
        d.RegisterField("ShooterId", (GuiderDefinition x) => x.ShooterId).Description("the guide camera, as a Shooter (not one of the scope's imaging shooters)");
        d.RegisterField("Output", (GuiderDefinition x) => x.Output).Description("Pulse (timed pulses to a GuidePort) | Correction (\"you are here, should be here\" to a GuideTarget)");
        d.RegisterField("GuidePortId", (GuiderDefinition x) => x.GuidePortId).Description("GuidePort device for Pulse output (a mount's pulse guiding, a camera's ST4 port)");
        d.RegisterField("TargetId", (GuiderDefinition x) => x.TargetId).Description("GuideTarget device for Correction output");
        d.RegisterField("MountId", (GuiderDefinition x) => x.MountId).Description("the Mount it rides on: declination and pier side for the calibration; empty = unknown");
        d.RegisterField("ExposureSeconds", (GuiderDefinition x) => x.ExposureSeconds).Range(0.01m, 60m);
        d.RegisterField("PixelScaleArcsec", (GuiderDefinition x) => x.PixelScaleArcsec).Description("guide camera arcsec per pixel; 0 = from the solve or the FITS header (FOCALLEN, PIXSIZE)");
        d.RegisterField("CameraAngleDegrees", (GuiderDefinition x) => x.CameraAngleDegrees).Description("where the guide image's up points, east of north; NaN = from the solve or the pulse calibration");
        d.RegisterField("SolveOrientation", (GuiderDefinition x) => x.SolveOrientation).Description("plate solve the first guide frame (if a solver is on the mesh) to know absolute positions");
        d.RegisterField("RaAggressiveness", (GuiderDefinition x) => x.RaAggressiveness).Range(0m, 1.5m);
        d.RegisterField("DecAggressiveness", (GuiderDefinition x) => x.DecAggressiveness).Range(0m, 1.5m);
        d.RegisterField("MinMovePixels", (GuiderDefinition x) => x.MinMovePixels).Description("errors smaller than this are left alone (seeing)");
        d.RegisterField("MaxPulseMs", (GuiderDefinition x) => x.MaxPulseMs);
        d.RegisterField("CalibrationStepMs", (GuiderDefinition x) => x.CalibrationStepMs);
        d.RegisterField("CalibrationPixels", (GuiderDefinition x) => x.CalibrationPixels).Description("how far the stars must move on each calibration leg");
        d.RegisterField("DecMode", (GuiderDefinition x) => x.DecMode).Description("Auto | North | South | Off");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class GuiderState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleBool Calibrated { get; set; } = false;
    public BinaryConvertibleString Calibration { get; set; } = "";
    public BinaryConvertibleBool Settled { get; set; } = false;
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleInt32 Stars { get; set; } = 0;
    public BinaryConvertibleInt32 Dithers { get; set; } = 0;
    public BinaryConvertibleDouble RmsRaArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble RmsDecArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble RmsTotalArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble ErrorPixels { get; set; } = double.NaN;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = double.NaN;
    public BinaryConvertibleString Output { get; set; } = "";

    public override string Name => "GuiderState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuiderState()
    {
        d.RegisterField("Phase", (GuiderState x) => x.Phase).Description("Idle | Acquiring | Calibrating | Guiding | Lost | Error");
        d.RegisterField("Message", (GuiderState x) => x.Message);
        d.RegisterField("Calibrated", (GuiderState x) => x.Calibrated).Description("pulse calibration known (Pulse output)");
        d.RegisterField("Calibration", (GuiderState x) => x.Calibration).Description("human-readable calibration summary");
        d.RegisterField("Settled", (GuiderState x) => x.Settled).Description("the error has stayed within the settle limit long enough");
        d.RegisterField("Frames", (GuiderState x) => x.Frames).Description("guide frames since the start");
        d.RegisterField("Stars", (GuiderState x) => x.Stars).Description("stars matched in the last frame");
        d.RegisterField("Dithers", (GuiderState x) => x.Dithers);
        d.RegisterField("RmsRaArcsec", (GuiderState x) => x.RmsRaArcsec).Description("over the last 50 frames; NaN without a pixel scale");
        d.RegisterField("RmsDecArcsec", (GuiderState x) => x.RmsDecArcsec);
        d.RegisterField("RmsTotalArcsec", (GuiderState x) => x.RmsTotalArcsec);
        d.RegisterField("ErrorPixels", (GuiderState x) => x.ErrorPixels).Description("distance from the lock in the last frame");
        d.RegisterField("PixelScaleArcsec", (GuiderState x) => x.PixelScaleArcsec);
        d.RegisterField("Output", (GuiderState x) => x.Output).Description("Pulse | Correction");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>One guide frame's measurement and what was done about it, for graphs and logs.</summary>
public class GuideStep : IBinaryConvertible
{
    public BinaryConvertibleInt32 Frame { get; set; } = 0;
    public BinaryConvertibleString Utc { get; set; } = "";
    public BinaryConvertibleDouble ErrorXPixels { get; set; } = 0.0;
    public BinaryConvertibleDouble ErrorYPixels { get; set; } = 0.0;
    public BinaryConvertibleDouble RaArcsec { get; set; } = double.NaN;
    public BinaryConvertibleDouble DecArcsec { get; set; } = double.NaN;
    public BinaryConvertibleInt32 RaPulseMs { get; set; } = 0;
    public BinaryConvertibleInt32 DecPulseMs { get; set; } = 0;
    public BinaryConvertibleInt32 Stars { get; set; } = 0;
    public BinaryConvertibleBool Settling { get; set; } = false;

    public override string Name => "GuideStep";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuideStep()
    {
        d.RegisterField("Frame", (GuideStep x) => x.Frame);
        d.RegisterField("Utc", (GuideStep x) => x.Utc);
        d.RegisterField("ErrorXPixels", (GuideStep x) => x.ErrorXPixels).Description("star position minus where it should be, guide camera pixels");
        d.RegisterField("ErrorYPixels", (GuideStep x) => x.ErrorYPixels);
        d.RegisterField("RaArcsec", (GuideStep x) => x.RaArcsec).Description("the error along the RA axis; NaN without scale and calibration");
        d.RegisterField("DecArcsec", (GuideStep x) => x.DecArcsec);
        d.RegisterField("RaPulseMs", (GuideStep x) => x.RaPulseMs).Description("pulse sent: + west, - east");
        d.RegisterField("DecPulseMs", (GuideStep x) => x.DecPulseMs).Description("pulse sent: + north, - south");
        d.RegisterField("Stars", (GuideStep x) => x.Stars);
        d.RegisterField("Settling", (GuideStep x) => x.Settling);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class GuideStartRequest : IBinaryConvertible
{
    public BinaryConvertibleBool Recalibrate { get; set; } = false;

    public override string Name => "GuideStartRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GuideStartRequest() { d.RegisterField("Recalibrate", (GuideStartRequest x) => x.Recalibrate).Description("calibrate even if a calibration is known"); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Move the lock point by a random amount (Pixels &gt; 0) and/or wait until guiding has settled.</summary>
public class DitherRequest : IBinaryConvertible
{
    public BinaryConvertibleDouble Pixels { get; set; } = 5.0;
    public BinaryConvertibleBool RaOnly { get; set; } = false;
    public BinaryConvertibleDouble SettlePixels { get; set; } = 1.5;
    public BinaryConvertibleDouble SettleSeconds { get; set; } = 10.0;
    public BinaryConvertibleDouble TimeoutSeconds { get; set; } = 90.0;

    public override string Name => "DitherRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DitherRequest()
    {
        d.RegisterField("Pixels", (DitherRequest x) => x.Pixels).Description("largest move of the lock, guide camera pixels; 0 = only wait until settled");
        d.RegisterField("RaOnly", (DitherRequest x) => x.RaOnly);
        d.RegisterField("SettlePixels", (DitherRequest x) => x.SettlePixels).Description("settled: the error stays below this ...");
        d.RegisterField("SettleSeconds", (DitherRequest x) => x.SettleSeconds).Description("... for this long");
        d.RegisterField("TimeoutSeconds", (DitherRequest x) => x.TimeoutSeconds).Description("give up waiting (guiding goes on)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class GuiderIds
{
    public const string Kind = "Guider";
    public static string State(string id) => EquipmentIds.State(Kind, id);
    public static string GetState(string id) => EquipmentIds.GetState(Kind, id);
    /// <summary>GuideStartRequest in, CommandResult out (accepted; progress in the state).</summary>
    public static string Start(string id) => EquipmentIds.Command(Kind, id, "Start");
    public static string Stop(string id) => EquipmentIds.Command(Kind, id, "Stop");
    /// <summary>DitherRequest in, CommandResult out when settled (or not, after the timeout).</summary>
    public static string Dither(string id) => EquipmentIds.Command(Kind, id, "Dither");
    public static string ClearCalibration(string id) => EquipmentIds.Command(Kind, id, "ClearCalibration");
    /// <summary>Event: GuideStep, every guide frame.</summary>
    public static string Step(string id) => EquipmentIds.Root(Kind, id) + ".Step";
    /// <summary>Event: GuideCorrection, every guide frame, in either output mode: anyone may close the loop with it.</summary>
    public static string Correction(string id) => EquipmentIds.Root(Kind, id) + ".Correction";
}

public static class GuidePortIds
{
    public const string Kind = "GuidePort";
    /// <summary>GuidePulse in, CommandResult out once the pulse has been played.</summary>
    public static string Pulse(string id) => EquipmentIds.Command(Kind, id, "Pulse");
    public static string State(string id) => EquipmentIds.State(Kind, id);
}
