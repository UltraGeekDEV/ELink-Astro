using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>"I want this image of this part of the sky": an area, a depth, and the scopes that may work on it. A single
/// target is just an area no bigger than a frame. Every scope pulls its next spot from one shared coverage plan as
/// soon as it is free (they are never kept in step), and does its own guiding, dithering, flips and focus on the way.
/// Frame sizes come from the scopes' imaging trains.</summary>
public class ImagingRequest : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "Image";
    public SkyTarget Center { get; set; } = new();
    public BinaryConvertibleDouble WidthDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble HeightDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble PositionAngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleCollection<BinaryConvertibleString> ScopeIds { get; set; } = new();
    public ShooterExposure Exposure { get; set; } = new();
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble StepoverDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble DitherArcsec { get; set; } = 30.0;
    public BinaryConvertibleInt32 MaxVisits { get; set; } = 0;
    public BinaryConvertibleString WeatherId { get; set; } = "";
    public BinaryConvertibleBool LiveStack { get; set; } = true;
    public BinaryConvertibleDouble OutputPixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble SlewTimeoutSeconds { get; set; } = 300.0;

    public override string Name => "ImagingRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingRequest()
    {
        d.RegisterField("Label", (ImagingRequest x) => x.Label).Description("names the image; frames are stamped with it");
        d.RegisterField("Center", (ImagingRequest x) => x.Center).Description("centre of the image, J2000");
        d.RegisterField("WidthDegrees", (ImagingRequest x) => x.WidthDegrees).Description("0 = one frame of the smallest train");
        d.RegisterField("HeightDegrees", (ImagingRequest x) => x.HeightDegrees);
        d.RegisterField("PositionAngleDegrees", (ImagingRequest x) => x.PositionAngleDegrees).Description("where the image's up points, east of north");
        d.RegisterField("ScopeIds", (ImagingRequest x) => x.ScopeIds, maxCount: 16).Description("the scopes that work on it; each pulls work when free");
        d.RegisterField("Exposure", (ImagingRequest x) => x.Exposure).Description("one shot: seconds, filter, frame type, binning");
        d.RegisterField("TargetSeconds", (ImagingRequest x) => x.TargetSeconds).Description("exposure every spot of the area should get; 0 = until stopped");
        d.RegisterField("StepoverDegrees", (ImagingRequest x) => x.StepoverDegrees).Description("largest hop between shots; 0 = a tenth of the smallest frame");
        d.RegisterField("DitherArcsec", (ImagingRequest x) => x.DitherArcsec).Description("every shot lands up to this far from its planned spot (unguided dithering; guided scopes also dither by themselves)");
        d.RegisterField("MaxVisits", (ImagingRequest x) => x.MaxVisits).Description("stop after this many shots in all; 0 = no limit");
        d.RegisterField("WeatherId", (ImagingRequest x) => x.WeatherId).Description("hold while this weather device reports Unsafe");
        d.RegisterField("LiveStack", (ImagingRequest x) => x.LiveStack).Description("build the image as it is taken (live stack of all the scopes' frames)");
        d.RegisterField("OutputPixelScaleArcsec", (ImagingRequest x) => x.OutputPixelScaleArcsec).Description("the image's pixel scale; 0 = the finest train's");
        d.RegisterField("SlewTimeoutSeconds", (ImagingRequest x) => x.SlewTimeoutSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>One frame of a scope, as placed on the area: size, turn and offset from the point the scope centres.</summary>
public class FrameFootprint : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble WidthDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble HeightDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble RotationDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetEastDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetNorthDegrees { get; set; } = 0.0;

    public override string Name => "FrameFootprint";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FrameFootprint()
    {
        d.RegisterField("Label", (FrameFootprint x) => x.Label);
        d.RegisterField("WidthDegrees", (FrameFootprint x) => x.WidthDegrees).Description("field of view along the frame's x axis");
        d.RegisterField("HeightDegrees", (FrameFootprint x) => x.HeightDegrees).Description("field of view along the frame's y axis");
        d.RegisterField("RotationDegrees", (FrameFootprint x) => x.RotationDegrees).Description("how the frame is turned on the scope (position angle, north through east)");
        d.RegisterField("OffsetEastDegrees", (FrameFootprint x) => x.OffsetEastDegrees).Description("where its centre is, along the scope's own east axis, from the point the scope centres");
        d.RegisterField("OffsetNorthDegrees", (FrameFootprint x) => x.OffsetNorthDegrees).Description("same, along the scope's north axis");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Slowly paint a virtual field of view with exposure time: many single shots in small stepovers, always toward the
/// least-covered part, until every spot has received TargetSeconds in total.</summary>

/// <summary>What one scope is doing for the request.</summary>
public class ImagingWorker : IBinaryConvertible
{
    public BinaryConvertibleString ScopeId { get; set; } = "";
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Visits { get; set; } = 0;
    public BinaryConvertibleDouble PoseX { get; set; } = double.NaN;
    public BinaryConvertibleDouble PoseY { get; set; } = double.NaN;
    public BinaryConvertibleCollection<FrameFootprint> Frames { get; set; } = new();

    public override string Name => "ImagingWorker";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingWorker()
    {
        d.RegisterField("ScopeId", (ImagingWorker x) => x.ScopeId);
        d.RegisterField("Phase", (ImagingWorker x) => x.Phase).Description("Waiting | Shooting | Done | Failed");
        d.RegisterField("Message", (ImagingWorker x) => x.Message);
        d.RegisterField("Visits", (ImagingWorker x) => x.Visits).Description("shots this scope finished");
        d.RegisterField("PoseX", (ImagingWorker x) => x.PoseX).Description("where it is shooting, degrees along the area's width from its centre");
        d.RegisterField("PoseY", (ImagingWorker x) => x.PoseY);
        d.RegisterField("Frames", (ImagingWorker x) => x.Frames, maxCount: 16).Description("its frames, from its imaging trains");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ImagingState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Visits { get; set; } = 0;
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MinSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MeanSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MaxSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble WidthDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble HeightDegrees { get; set; } = 0.0;
    public BinaryConvertibleInt32 MapCols { get; set; } = 0;
    public BinaryConvertibleInt32 MapRows { get; set; } = 0;
    public RawBytes Map { get; set; } = new();
    public BinaryConvertibleCollection<ImagingWorker> Workers { get; set; } = new();

    public override string Name => "ImagingState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingState()
    {
        d.RegisterField("Phase", (ImagingState x) => x.Phase).Description("Idle | Running | Paused | WaitingForWeather | Done | Aborted | Error");
        d.RegisterField("Label", (ImagingState x) => x.Label);
        d.RegisterField("Message", (ImagingState x) => x.Message);
        d.RegisterField("Visits", (ImagingState x) => x.Visits).Description("shots finished, all scopes");
        d.RegisterField("TargetSeconds", (ImagingState x) => x.TargetSeconds);
        d.RegisterField("MinSeconds", (ImagingState x) => x.MinSeconds).Description("least exposed spot of the area");
        d.RegisterField("MeanSeconds", (ImagingState x) => x.MeanSeconds);
        d.RegisterField("MaxSeconds", (ImagingState x) => x.MaxSeconds);
        d.RegisterField("WidthDegrees", (ImagingState x) => x.WidthDegrees).Description("the area as resolved (a request of 0 takes one frame)");
        d.RegisterField("HeightDegrees", (ImagingState x) => x.HeightDegrees);
        d.RegisterField("MapCols", (ImagingState x) => x.MapCols);
        d.RegisterField("MapRows", (ImagingState x) => x.MapRows);
        d.RegisterField("Map", (ImagingState x) => x.Map).Description("coverage, one byte per cell (0..255 of the target), row-major from the north-west");
        d.RegisterField("Workers", (ImagingState x) => x.Workers, maxCount: 16);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ImagingIds
{
    public const string Root = "ELink.Automation.Imaging";
    /// <summary>ImagingRequest in, CommandResult out (accepted; progress in the state).</summary>
    public const string Start = Root + ".Start";
    /// <summary>ImagingRequest in, ImagingState out: the frames each scope would use and the resolved area, without running.</summary>
    public const string Preview = Root + ".Preview";
    public const string Pause = Root + ".Pause";
    public const string Resume = Root + ".Resume";
    public const string Abort = Root + ".Abort";
    /// <summary>BinaryConvertibleDouble (seconds) in: change the depth while running.</summary>
    public const string SetTarget = Root + ".SetTarget";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
