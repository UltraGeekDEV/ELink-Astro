using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>A master calibration frame in the library: the combination of many darks, biases or flats of one camera.</summary>
public class CalibrationMaster : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "Dark";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleDouble TemperatureC { get; set; } = double.NaN;
    public BinaryConvertibleInt32 BinX { get; set; } = 1;
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;
    public BinaryConvertibleString CreatedUtc { get; set; } = "";

    public override string Name => "CalibrationMaster";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CalibrationMaster()
    {
        d.RegisterField("Id", (CalibrationMaster x) => x.Id);
        d.RegisterField("ShooterId", (CalibrationMaster x) => x.ShooterId).Description("the camera's Shooter (frames say which shooter took them)");
        d.RegisterField("Kind", (CalibrationMaster x) => x.Kind).Description("Dark | Bias | Flat");
        d.RegisterField("ExposureSeconds", (CalibrationMaster x) => x.ExposureSeconds);
        d.RegisterField("Gain", (CalibrationMaster x) => x.Gain).Description("NaN = not known");
        d.RegisterField("Iso", (CalibrationMaster x) => x.Iso);
        d.RegisterField("TemperatureC", (CalibrationMaster x) => x.TemperatureC).Description("sensor temperature while taking them; NaN = not known");
        d.RegisterField("BinX", (CalibrationMaster x) => x.BinX);
        d.RegisterField("Filter", (CalibrationMaster x) => x.Filter).Description("flats: the filter they were taken through");
        d.RegisterField("Frames", (CalibrationMaster x) => x.Frames);
        d.RegisterField("Width", (CalibrationMaster x) => x.Width);
        d.RegisterField("Height", (CalibrationMaster x) => x.Height);
        d.RegisterField("CreatedUtc", (CalibrationMaster x) => x.CreatedUtc);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class CalibrationMasters : IBinaryConvertible
{
    public BinaryConvertibleCollection<CalibrationMaster> Masters { get; set; } = new();

    public override string Name => "CalibrationMasters";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CalibrationMasters() { d.RegisterField("Masters", (CalibrationMasters x) => x.Masters, maxCount: 500); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Take calibration frames with a camera and make a master of them.</summary>
public class CaptureRequest : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "Dark";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleInt32 Count { get; set; } = 20;
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleInt32 BinX { get; set; } = 0;
    public BinaryConvertibleDouble FlatTargetFraction { get; set; } = 0.5;

    public override string Name => "CaptureRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CaptureRequest()
    {
        d.RegisterField("ShooterId", (CaptureRequest x) => x.ShooterId).Description("one camera's Shooter (a train's camera: <train>-<camera>)");
        d.RegisterField("Kind", (CaptureRequest x) => x.Kind).Description("Dark (cap on) | Bias (shortest exposure, cap on) | Flat (even light: panel or twilight sky)");
        d.RegisterField("ExposureSeconds", (CaptureRequest x) => x.ExposureSeconds).Description("darks: as the lights; flats: 0 = find the exposure that reaches the target level");
        d.RegisterField("Count", (CaptureRequest x) => x.Count).Range(3, 500);
        d.RegisterField("Filter", (CaptureRequest x) => x.Filter).Description("flats: through this filter");
        d.RegisterField("Gain", (CaptureRequest x) => x.Gain).Description("NaN = as it is");
        d.RegisterField("Iso", (CaptureRequest x) => x.Iso);
        d.RegisterField("BinX", (CaptureRequest x) => x.BinX).Description("0 = as it is");
        d.RegisterField("FlatTargetFraction", (CaptureRequest x) => x.FlatTargetFraction).Description("flats: the level to reach, as a share of the camera's range");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Which master suits a light frame.</summary>
public class MasterQuery : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "Dark";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble Gain { get; set; } = double.NaN;
    public BinaryConvertibleString Iso { get; set; } = "";
    public BinaryConvertibleDouble TemperatureC { get; set; } = double.NaN;
    public BinaryConvertibleInt32 BinX { get; set; } = 1;
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;

    public override string Name => "MasterQuery";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MasterQuery()
    {
        d.RegisterField("ShooterId", (MasterQuery x) => x.ShooterId);
        d.RegisterField("Kind", (MasterQuery x) => x.Kind).Description("Dark (a dark, else a bias) | Flat");
        d.RegisterField("ExposureSeconds", (MasterQuery x) => x.ExposureSeconds);
        d.RegisterField("Gain", (MasterQuery x) => x.Gain);
        d.RegisterField("Iso", (MasterQuery x) => x.Iso);
        d.RegisterField("TemperatureC", (MasterQuery x) => x.TemperatureC);
        d.RegisterField("BinX", (MasterQuery x) => x.BinX);
        d.RegisterField("Filter", (MasterQuery x) => x.Filter);
        d.RegisterField("Width", (MasterQuery x) => x.Width).Description("of the light frame: a master must be the same size");
        d.RegisterField("Height", (MasterQuery x) => x.Height);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class MasterMatch : IBinaryConvertible
{
    public BinaryConvertibleBool Found { get; set; } = false;
    public CalibrationMaster Master { get; set; } = new();
    public RawBytes Image { get; set; } = new();
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "MasterMatch";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MasterMatch()
    {
        d.RegisterField("Found", (MasterMatch x) => x.Found);
        d.RegisterField("Master", (MasterMatch x) => x.Master);
        d.RegisterField("Image", (MasterMatch x) => x.Image).Description("32-bit float FITS; flats are normalised to a median of 1");
        d.RegisterField("Message", (MasterMatch x) => x.Message).Description("why nothing suits");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class CalibrationState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Done { get; set; } = 0;
    public BinaryConvertibleInt32 Count { get; set; } = 0;
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;

    public override string Name => "CalibrationState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CalibrationState()
    {
        d.RegisterField("Phase", (CalibrationState x) => x.Phase).Description("Idle | FindingExposure | Capturing | Combining | Done | Error | Aborted");
        d.RegisterField("Message", (CalibrationState x) => x.Message);
        d.RegisterField("Done", (CalibrationState x) => x.Done);
        d.RegisterField("Count", (CalibrationState x) => x.Count);
        d.RegisterField("ExposureSeconds", (CalibrationState x) => x.ExposureSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class CalibrationIds
{
    public const string Root = "ELink.Automation.Calibration";
    /// <summary>CaptureRequest in, CommandResult out (accepted; progress in the state).</summary>
    public const string Capture = Root + ".Capture";
    public const string Abort = Root + ".Abort";
    /// <summary>Void in, CalibrationMasters out.</summary>
    public const string List = Root + ".List";
    /// <summary>BinaryConvertibleString (id) in.</summary>
    public const string Delete = Root + ".Delete";
    /// <summary>MasterQuery in, MasterMatch out.</summary>
    public const string Find = Root + ".Find";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
