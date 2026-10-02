using ELink.Contracts;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Camera status. Commands under ELink.Camera.&lt;Id&gt;.: Connect(bool), Expose(<see cref="ExposeRequest"/>),
/// AbortExposure(Void), SetTemperature(double), SetCooler(bool). Finished frames arrive on <c>.Frame</c> (<see cref="FrameEvent"/>).</summary>
public class CameraState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleDouble ExposureRemaining { get; set; } = 0.0;
    public BinaryConvertibleDouble Temperature { get; set; } = double.NaN;
    public BinaryConvertibleBool HasCooler { get; set; } = false;
    public BinaryConvertibleInt32 SensorWidth { get; set; } = 0;
    public BinaryConvertibleInt32 SensorHeight { get; set; } = 0;
    public BinaryConvertibleDouble PixelSizeUm { get; set; } = 0.0;
    public BinaryConvertibleInt32 BitsPerPixel { get; set; } = 0;
    public BinaryConvertibleInt32 BinX { get; set; } = 1;
    public BinaryConvertibleInt32 BinY { get; set; } = 1;
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleCollection<BinaryConvertibleString> IsoChoices { get; set; } = new();
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleString TransferFormat { get; set; } = "";
    public BinaryConvertibleDouble Offset { get; set; } = double.NaN;
    public BinaryConvertibleBool CoolerOn { get; set; } = false;
    public BinaryConvertibleDouble CoolerPower { get; set; } = double.NaN;
    public BinaryConvertibleDouble TemperatureTarget { get; set; } = double.NaN;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "CameraState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CameraState()
    {
        d.RegisterField("Connected", (CameraState x) => x.Connected);
        d.RegisterField("Phase", (CameraState x) => x.Phase).Description("Disconnected | Idle | Exposing | Error");
        d.RegisterField("ExposureRemaining", (CameraState x) => x.ExposureRemaining).Description("seconds left of the running exposure");
        d.RegisterField("Temperature", (CameraState x) => x.Temperature).Description("sensor temperature in °C, NaN if the camera has no sensor thermometer");
        d.RegisterField("HasCooler", (CameraState x) => x.HasCooler);
        d.RegisterField("SensorWidth", (CameraState x) => x.SensorWidth).Description("pixels");
        d.RegisterField("SensorHeight", (CameraState x) => x.SensorHeight).Description("pixels");
        d.RegisterField("PixelSizeUm", (CameraState x) => x.PixelSizeUm).Description("pixel pitch in micrometres");
        d.RegisterField("BitsPerPixel", (CameraState x) => x.BitsPerPixel);
        d.RegisterField("BinX", (CameraState x) => x.BinX);
        d.RegisterField("BinY", (CameraState x) => x.BinY);
        d.RegisterField("Gain", (CameraState x) => x.Gain).Description("NaN if the camera has no gain control");
        d.RegisterField("IsoChoices", (CameraState x) => x.IsoChoices, maxCount: 64).Description("DSLRs: the ISO settings the camera offers (INDI CCD_ISO); empty otherwise");
        d.RegisterField("Iso", (CameraState x) => x.Iso).Description("the ISO in use");
        d.RegisterField("TransferFormat", (CameraState x) => x.TransferFormat).Description("FITS | Native | empty (INDI CCD_TRANSFER_FORMAT); ELink asks for FITS before exposing");
        d.RegisterField("Offset", (CameraState x) => x.Offset).Description("NaN if the camera has no offset control");
        d.RegisterField("CoolerOn", (CameraState x) => x.CoolerOn);
        d.RegisterField("CoolerPower", (CameraState x) => x.CoolerPower).Description("percent; NaN = not reported");
        d.RegisterField("TemperatureTarget", (CameraState x) => x.TemperatureTarget).Description("the set point last asked for; NaN = none");
        d.RegisterField("Message", (CameraState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ExposeRequest : IBinaryConvertible
{
    public BinaryConvertibleDouble Seconds { get; set; } = 1.0;
    public BinaryConvertibleString FrameType { get; set; } = "Light";
    public BinaryConvertibleInt32 BinX { get; set; } = 0;
    public BinaryConvertibleInt32 BinY { get; set; } = 0;
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleDouble Offset { get; set; } = double.NaN;

    public override string Name => "ExposeRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ExposeRequest()
    {
        d.RegisterField("Seconds", (ExposeRequest x) => x.Seconds).Description("exposure time").Range(0, 86400);
        d.RegisterField("FrameType", (ExposeRequest x) => x.FrameType).Description("Light | Dark | Bias | Flat");
        d.RegisterField("BinX", (ExposeRequest x) => x.BinX).Description("0 = leave the camera's current binning");
        d.RegisterField("BinY", (ExposeRequest x) => x.BinY).Description("0 = leave the camera's current binning");
        d.RegisterField("Gain", (ExposeRequest x) => x.Gain).Description("NaN = leave the camera's current gain");
        d.RegisterField("Iso", (ExposeRequest x) => x.Iso).Description("DSLRs: e.g. 800 or ISO800; empty = leave it");
        d.RegisterField("Offset", (ExposeRequest x) => x.Offset).Description("NaN = leave the camera's current offset");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A finished exposure.</summary>
public class FrameEvent : IBinaryConvertible
{
    public BinaryConvertibleString Device { get; set; } = "";
    public BinaryConvertibleString Format { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleString FrameType { get; set; } = "";
    public BinaryConvertibleString Timestamp { get; set; } = "";
    public RawBytes Data { get; set; } = new();

    public override string Name => "FrameEvent";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FrameEvent()
    {
        d.RegisterField("Device", (FrameEvent x) => x.Device);
        d.RegisterField("Format", (FrameEvent x) => x.Format).Description(".fits, .xisf, .jpg ... never compressed: the bridge inflates .z payloads");
        d.RegisterField("ExposureSeconds", (FrameEvent x) => x.ExposureSeconds);
        d.RegisterField("FrameType", (FrameEvent x) => x.FrameType);
        d.RegisterField("Timestamp", (FrameEvent x) => x.Timestamp).Description("ISO 8601 UTC, when the bridge received the frame");
        d.RegisterField("Data", (FrameEvent x) => x.Data);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A camera's sensor, for drivers that cannot tell (DSLRs through gphoto): INDI CCD_INFO.</summary>
public class SensorInfo : IBinaryConvertible
{
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;
    public BinaryConvertibleDouble PixelSizeUm { get; set; } = 0.0;
    public BinaryConvertibleInt32 BitsPerPixel { get; set; } = 0;

    public override string Name => "SensorInfo";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SensorInfo()
    {
        d.RegisterField("Width", (SensorInfo x) => x.Width).Description("pixels; 0 = leave");
        d.RegisterField("Height", (SensorInfo x) => x.Height);
        d.RegisterField("PixelSizeUm", (SensorInfo x) => x.PixelSizeUm).Description("micrometres; 0 = leave");
        d.RegisterField("BitsPerPixel", (SensorInfo x) => x.BitsPerPixel).Description("0 = leave");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
