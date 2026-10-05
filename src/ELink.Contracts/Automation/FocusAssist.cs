using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Take frames over and over and measure their focus while a person focuses by hand.</summary>
public class FocusAssistRequest : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 2.0;
    public BinaryConvertibleString Filter { get; set; } = "";

    public override string Name => "FocusAssistRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusAssistRequest()
    {
        d.RegisterField("ShooterId", (FocusAssistRequest x) => x.ShooterId).Description("the camera (shooter) that takes the frames");
        d.RegisterField("ExposureSeconds", (FocusAssistRequest x) => x.ExposureSeconds).Description("short: the numbers should follow the focus knob");
        d.RegisterField("Filter", (FocusAssistRequest x) => x.Filter).Description("filter to shoot through, empty = as is (a pseudo mono camera with a focuser: \"Raw\")");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>What one frame says about focus. Colour cameras (a Bayer pattern in the frame) are measured colour by colour.</summary>
public class FocusAssistSample : IBinaryConvertible
{
    public BinaryConvertibleDouble Hfr { get; set; } = double.NaN;
    public BinaryConvertibleInt32 Stars { get; set; } = 0;
    public BinaryConvertibleDouble Elongation { get; set; } = double.NaN;
    public BinaryConvertibleDouble HfrRed { get; set; } = double.NaN;
    public BinaryConvertibleDouble HfrGreen { get; set; } = double.NaN;
    public BinaryConvertibleDouble HfrBlue { get; set; } = double.NaN;
    public BinaryConvertibleDouble Peak { get; set; } = double.NaN;

    public override string Name => "FocusAssistSample";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusAssistSample()
    {
        d.RegisterField("Hfr", (FocusAssistSample x) => x.Hfr).Description("median half-flux radius of the stars, pixels (colour frames: the mean of the colours'; their pixels are half the size of the sensor's)");
        d.RegisterField("Stars", (FocusAssistSample x) => x.Stars);
        d.RegisterField("Elongation", (FocusAssistSample x) => x.Elongation).Description("1 = round");
        d.RegisterField("HfrRed", (FocusAssistSample x) => x.HfrRed);
        d.RegisterField("HfrGreen", (FocusAssistSample x) => x.HfrGreen);
        d.RegisterField("HfrBlue", (FocusAssistSample x) => x.HfrBlue);
        d.RegisterField("Peak", (FocusAssistSample x) => x.Peak).Description("brightest star's peak as a fraction of the camera's range: near 1 the stars are saturated and HFR is unreliable");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class FocusAssistState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleDouble BestHfr { get; set; } = double.NaN;
    public BinaryConvertibleString Trend { get; set; } = "";
    public BinaryConvertibleCollection<FocusAssistSample> Samples { get; set; } = new();

    public override string Name => "FocusAssistState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusAssistState()
    {
        d.RegisterField("Phase", (FocusAssistState x) => x.Phase).Description("Idle | Running | Error");
        d.RegisterField("Message", (FocusAssistState x) => x.Message);
        d.RegisterField("ShooterId", (FocusAssistState x) => x.ShooterId);
        d.RegisterField("Frames", (FocusAssistState x) => x.Frames).Description("measured since it was started or reset");
        d.RegisterField("BestHfr", (FocusAssistState x) => x.BestHfr).Description("the sharpest frame since it was started or reset");
        d.RegisterField("Trend", (FocusAssistState x) => x.Trend).Description("sharper | softer | steady, the last few frames against the ones before");
        d.RegisterField("Samples", (FocusAssistState x) => x.Samples, maxCount: 120).Description("the latest frames, oldest first");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class FocusAssistIds
{
    public const string Root = "ELink.Automation.FocusAssist";
    /// <summary>FocusAssistRequest in, CommandResult out: start taking and measuring frames until stopped.</summary>
    public const string Start = Root + ".Start";
    public const string Stop = Root + ".Stop";
    /// <summary>Void in: forget the history (and the best), e.g. after moving to another star.</summary>
    public const string Reset = Root + ".Reset";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
