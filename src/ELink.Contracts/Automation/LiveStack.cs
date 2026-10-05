using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Live stacking into a fixed field: frames from the given shooters are registered on the sky (plate solved,
/// their own WCS, or the pointing) and resampled into one image of the requested area and pixel scale.</summary>
public class LiveStackRequest : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "Stack";
    public BinaryConvertibleCollection<BinaryConvertibleString> ShooterIds { get; set; } = new();
    public SkyTarget Center { get; set; } = new();
    public BinaryConvertibleDouble FovWidthDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble FovHeightDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble PositionAngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleString Interpolation { get; set; } = "Bicubic";
    public BinaryConvertibleString Registration { get; set; } = "Auto";
    public BinaryConvertibleDouble FramePixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble FramePositionAngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleBool NormalizeBackground { get; set; } = true;
    public BinaryConvertibleDouble SolveTimeoutSeconds { get; set; } = 60.0;
    public BinaryConvertibleDouble MaxMegapixels { get; set; } = 40.0;
    public BinaryConvertibleString Debayer { get; set; } = "Interpolated";
    public BinaryConvertibleBool SeparateFilters { get; set; } = true;
    public BinaryConvertibleString SessionKey { get; set; } = "";
    public BinaryConvertibleBool Resume { get; set; } = true;
    public BinaryConvertibleCollection<ImagingLayer> Layers { get; set; } = new();
    public BinaryConvertibleBool MatchFlux { get; set; } = true;
    public BinaryConvertibleDouble RejectSigma { get; set; } = 3.0;
    public BinaryConvertibleString BayerPattern { get; set; } = "";
    public BinaryConvertibleBool Calibrate { get; set; } = true;

    public override string Name => "LiveStackRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LiveStackRequest()
    {
        d.RegisterField("Label", (LiveStackRequest x) => x.Label);
        d.RegisterField("ShooterIds", (LiveStackRequest x) => x.ShooterIds, maxCount: 32).Description("stack the Light frames of these shooters (a scope id takes all of its frames)");
        d.RegisterField("Center", (LiveStackRequest x) => x.Center).Description("centre of the stacked field, J2000 (e.g. the mosaic's virtual FOV)");
        d.RegisterField("FovWidthDegrees", (LiveStackRequest x) => x.FovWidthDegrees).Description("width of the stacked field, along its own x axis");
        d.RegisterField("FovHeightDegrees", (LiveStackRequest x) => x.FovHeightDegrees);
        d.RegisterField("PositionAngleDegrees", (LiveStackRequest x) => x.PositionAngleDegrees).Description("where the stack's up points, degrees east of north");
        d.RegisterField("PixelScaleArcsec", (LiveStackRequest x) => x.PixelScaleArcsec).Description("output arcsec per pixel: finer than the frames interpolates, coarser area-averages; 0 = the first frame's scale");
        d.RegisterField("Interpolation", (LiveStackRequest x) => x.Interpolation).Description("Bicubic | Bilinear | Nearest, for output finer than the frames");
        d.RegisterField("Registration", (LiveStackRequest x) => x.Registration).Description("Auto (frame WCS, else solve, else pointing) | Solve | Pointing");
        d.RegisterField("FramePixelScaleArcsec", (LiveStackRequest x) => x.FramePixelScaleArcsec).Description("the frames' scale: needed for Pointing, a solve hint otherwise; 0 = unknown");
        d.RegisterField("FramePositionAngleDegrees", (LiveStackRequest x) => x.FramePositionAngleDegrees).Description("the frames' up, east of north, for Pointing");
        d.RegisterField("NormalizeBackground", (LiveStackRequest x) => x.NormalizeBackground).Description("match every frame's sky level to the first one before adding it");
        d.RegisterField("SolveTimeoutSeconds", (LiveStackRequest x) => x.SolveTimeoutSeconds);
        d.RegisterField("MaxMegapixels", (LiveStackRequest x) => x.MaxMegapixels).Description("refuse fields larger than this at the requested scale (8 bytes of memory per pixel, 16 in colour)");
        d.RegisterField("SessionKey", (LiveStackRequest x) => x.SessionKey).Description("keep this stack (saved every few minutes and when stopped) under this name; empty = not kept");
        d.RegisterField("Resume", (LiveStackRequest x) => x.Resume).Description("carry on a kept stack of this name (it must cover the same field); false = start it afresh");
        d.RegisterField("Layers", (LiveStackRequest x) => x.Layers, maxCount: 8).Description("with layers, one stack per layer: a frame goes to the first layer whose filter it was shot through and whose pixel scale range holds the frame's scale (a frame of none is left out); empty = one stack per filter");
        d.RegisterField("SeparateFilters", (LiveStackRequest x) => x.SeparateFilters).Description("one stack per filter (frames say which); false = all into one");
        d.RegisterField("MatchFlux", (LiveStackRequest x) => x.MatchFlux).Description("scale every frame so its stars match the stack (other cameras and scopes, thin cloud)");
        d.RegisterField("RejectSigma", (LiveStackRequest x) => x.RejectSigma).Description("leave out samples this many standard deviations from a pixel's mean (satellites, planes); 0 = keep all");
        d.RegisterField("Debayer", (LiveStackRequest x) => x.Debayer).Description("raw colour (Bayer) frames: Interpolated (full resolution) | SuperPixel (each 2x2 cell is one RGB pixel, half resolution) | None (stack the raw mosaic as mono)");
        d.RegisterField("BayerPattern", (LiveStackRequest x) => x.BayerPattern).Description("RGGB | BGGR | GRBG | GBRG as seen from the first pixel of the file; empty = the frame's BAYERPAT (and X/YBAYROFF)");
        d.RegisterField("Calibrate", (LiveStackRequest x) => x.Calibrate).Description("take off the dark (or bias) and divide by the flat from the calibration library, when it has ones that suit");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A frame pushed into the stack directly (from disk, another program, ...), instead of from a shooter.</summary>
public class LiveStackFrame : IBinaryConvertible
{
    public RawBytes Image { get; set; } = new();
    public BinaryConvertibleString Source { get; set; } = "";
    public BinaryConvertibleDouble PointingRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble PointingDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleString Filter { get; set; } = "";

    public override string Name => "LiveStackFrame";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LiveStackFrame()
    {
        d.RegisterField("Image", (LiveStackFrame x) => x.Image).Description("a mono FITS file");
        d.RegisterField("Source", (LiveStackFrame x) => x.Source).Description("where it came from, for the log");
        d.RegisterField("PointingRaHours", (LiveStackFrame x) => x.PointingRaHours).Description("J2000 hint, NaN = unknown");
        d.RegisterField("PointingDecDegrees", (LiveStackFrame x) => x.PointingDecDegrees);
        d.RegisterField("Filter", (LiveStackFrame x) => x.Filter).Description("empty = the FITS header's FILTER");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class LiveStackState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 FramesStacked { get; set; } = 0;
    public BinaryConvertibleInt32 FramesRejected { get; set; } = 0;
    public BinaryConvertibleInt32 FramesPending { get; set; } = 0;
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble CoveragePercent { get; set; } = 0.0;
    public BinaryConvertibleDouble TotalExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleString LastFrame { get; set; } = "";
    public BinaryConvertibleInt32 Channels { get; set; } = 0;
    public BinaryConvertibleCollection<BinaryConvertibleString> Filters { get; set; } = new();

    public override string Name => "LiveStackState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LiveStackState()
    {
        d.RegisterField("Phase", (LiveStackState x) => x.Phase).Description("Idle | Stacking | Stopped | Error");
        d.RegisterField("Label", (LiveStackState x) => x.Label);
        d.RegisterField("Message", (LiveStackState x) => x.Message).Description("what happened to the last frame");
        d.RegisterField("FramesStacked", (LiveStackState x) => x.FramesStacked);
        d.RegisterField("FramesRejected", (LiveStackState x) => x.FramesRejected).Description("could not be registered or missed the field");
        d.RegisterField("FramesPending", (LiveStackState x) => x.FramesPending).Description("received, waiting to be registered");
        d.RegisterField("Width", (LiveStackState x) => x.Width).Description("stack size in pixels; 0 until the scale is known");
        d.RegisterField("Height", (LiveStackState x) => x.Height);
        d.RegisterField("PixelScaleArcsec", (LiveStackState x) => x.PixelScaleArcsec);
        d.RegisterField("CoveragePercent", (LiveStackState x) => x.CoveragePercent).Description("share of the field that has data");
        d.RegisterField("TotalExposureSeconds", (LiveStackState x) => x.TotalExposureSeconds);
        d.RegisterField("LastFrame", (LiveStackState x) => x.LastFrame);
        d.RegisterField("Channels", (LiveStackState x) => x.Channels).Description("1 = mono, 3 = RGB; 0 until the stack exists");
        d.RegisterField("Filters", (LiveStackState x) => x.Filters, maxCount: 16).Description("one entry per filter stack: \"name: frames\"");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class LiveStackImageRequest : IBinaryConvertible
{
    public BinaryConvertibleInt32 MaxWidth { get; set; } = 0;
    public BinaryConvertibleInt32 MaxHeight { get; set; } = 0;
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleBool Neutralize { get; set; } = true;
    public BinaryConvertibleDouble OutOfFocusWeight { get; set; } = 0.0;
    public BinaryConvertibleString PseudoOutput { get; set; } = "";

    public override string Name => "LiveStackImageRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LiveStackImageRequest()
    {
        d.RegisterField("MaxWidth", (LiveStackImageRequest x) => x.MaxWidth).Description("area-average the stack down to fit; 0 = full size");
        d.RegisterField("MaxHeight", (LiveStackImageRequest x) => x.MaxHeight);
        d.RegisterField("Filter", (LiveStackImageRequest x) => x.Filter).Description("which filter's (or layer's) stack; \"Combined\" = the layers laid over each other, the finest detail over the coarse base; empty = the combined image when there are several layers, else the stack with the most frames");
        d.RegisterField("Neutralize", (LiveStackImageRequest x) => x.Neutralize).Description("colour stacks: even, neutral background and white-balanced stars");
        d.RegisterField("OutOfFocusWeight", (LiveStackImageRequest x) => x.OutOfFocusWeight).Description("pseudo mono: how much of the out-of-focus luminance goes into the colour image, 0 (none: the sharpest colours, the data is left out) to 1 (all: the most signal, with a soft halo)").Range(0m, 1m);
        d.RegisterField("PseudoOutput", (LiveStackImageRequest x) => x.PseudoOutput).Description("pseudo mono: Colour (default) | Luminance (all the out-of-focus light, mono) | Sharp (the in-focus colours added up, mono)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class LiveStackImage : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleInt32 Channels { get; set; } = 0;
    public BinaryConvertibleString Filter { get; set; } = "";
    public RawBytes Image { get; set; } = new();

    public override string Name => "LiveStackImage";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LiveStackImage()
    {
        d.RegisterField("Ok", (LiveStackImage x) => x.Ok);
        d.RegisterField("Message", (LiveStackImage x) => x.Message);
        d.RegisterField("Width", (LiveStackImage x) => x.Width);
        d.RegisterField("Height", (LiveStackImage x) => x.Height);
        d.RegisterField("PixelScaleArcsec", (LiveStackImage x) => x.PixelScaleArcsec);
        d.RegisterField("Frames", (LiveStackImage x) => x.Frames);
        d.RegisterField("Channels", (LiveStackImage x) => x.Channels).Description("1 = mono, 3 = RGB planes (NAXIS3)");
        d.RegisterField("Filter", (LiveStackImage x) => x.Filter);
        d.RegisterField("Image", (LiveStackImage x) => x.Image).Description("32-bit float FITS with the stack's WCS");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class LiveStackIds
{
    public const string Root = "ELink.Automation.LiveStack";
    /// <summary>LiveStackRequest in, CommandResult out: a new, empty stack that takes the shooters' frames from now on.</summary>
    public const string Start = Root + ".Start";
    /// <summary>Void in: stop taking frames (the stack is kept).</summary>
    public const string Stop = Root + ".Stop";
    /// <summary>Void in: empty the stack, keep the field and shooters.</summary>
    public const string Reset = Root + ".Reset";
    /// <summary>LiveStackFrame in, CommandResult out (queued, not yet stacked).</summary>
    public const string Add = Root + ".Add";
    /// <summary>LiveStackImageRequest in, LiveStackImage out.</summary>
    public const string GetImage = Root + ".GetImage";
    /// <summary>Void in: save a kept stack now (it also saves every few minutes and when stopped).</summary>
    public const string Save = Root + ".Save";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
