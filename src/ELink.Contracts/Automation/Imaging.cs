using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>One layer (channel) of an image: a kind of data with its own depth. A frame feeds a layer when it was shot through the
/// layer's filter and its pixel scale is within the layer's range. With layers a wide, fast scope can build a strong low resolution
/// base while a long focal length scope adds a high resolution one, each to the depth it needs, by itself.</summary>
public class ImagingLayer : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleDouble MinScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble MaxScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;

    public override string Name => "ImagingLayer";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingLayer()
    {
        d.RegisterField("Label", (ImagingLayer x) => x.Label).Description("names the layer (and its stack)");
        d.RegisterField("Filter", (ImagingLayer x) => x.Filter).Description("the filter its shots are taken through; empty = the request's exposure's");
        d.RegisterField("MinScaleArcsec", (ImagingLayer x) => x.MinScaleArcsec).Description("frames finer than this (arcseconds per pixel) do not feed it; 0 = no limit");
        d.RegisterField("MaxScaleArcsec", (ImagingLayer x) => x.MaxScaleArcsec).Description("frames coarser than this do not feed it; 0 = no limit");
        d.RegisterField("TargetSeconds", (ImagingLayer x) => x.TargetSeconds).Description("its depth; 0 = the request's");
        d.RegisterField("ExposureSeconds", (ImagingLayer x) => x.ExposureSeconds).Description("one shot of it; 0 = the request's");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

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
    public BinaryConvertibleDouble SkyWaitSeconds { get; set; } = 120.0;
    public BinaryConvertibleBool Resume { get; set; } = true;
    public BinaryConvertibleCollection<ImagingLayer> Layers { get; set; } = new();

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
        d.RegisterField("Resume", (ImagingRequest x) => x.Resume).Description("an image of this name was started before (another night): carry on with its coverage and stack; false = start it afresh");
        d.RegisterField("SkyWaitSeconds", (ImagingRequest x) => x.SkyWaitSeconds).Description("after 3 rejected shots in a row, a scope waits this long before the next (clouds passing)");
        d.RegisterField("Layers", (ImagingRequest x) => x.Layers, maxCount: 8).Description("kinds of data the image is made of, each with a filter, a range of pixel scales and a depth; empty = one layer of everything, shot as Exposure says");
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
    public BinaryConvertibleInt32 Rejected { get; set; } = 0;
    public BinaryConvertibleDouble PoseX { get; set; } = double.NaN;
    public BinaryConvertibleDouble PoseY { get; set; } = double.NaN;
    public BinaryConvertibleCollection<FrameFootprint> Frames { get; set; } = new();

    public override string Name => "ImagingWorker";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingWorker()
    {
        d.RegisterField("ScopeId", (ImagingWorker x) => x.ScopeId);
        d.RegisterField("Phase", (ImagingWorker x) => x.Phase).Description("Waiting | Shooting | WaitingForSky | Done | Failed");
        d.RegisterField("Message", (ImagingWorker x) => x.Message);
        d.RegisterField("Visits", (ImagingWorker x) => x.Visits).Description("good shots this scope finished");
        d.RegisterField("Rejected", (ImagingWorker x) => x.Rejected).Description("shots its scope rejected (clouds, trailing, ...): their spots went back to the plan");
        d.RegisterField("PoseX", (ImagingWorker x) => x.PoseX).Description("where it is shooting, degrees along the area's width from its centre");
        d.RegisterField("PoseY", (ImagingWorker x) => x.PoseY);
        d.RegisterField("Frames", (ImagingWorker x) => x.Frames, maxCount: 16).Description("its frames, from its imaging trains");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>How far one layer of the image has got.</summary>
public class ImagingLayerState : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Filter { get; set; } = "";
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MinSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MeanSeconds { get; set; } = 0.0;
    public BinaryConvertibleInt32 Scopes { get; set; } = 0;

    public override string Name => "ImagingLayerState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ImagingLayerState()
    {
        d.RegisterField("Label", (ImagingLayerState x) => x.Label);
        d.RegisterField("Filter", (ImagingLayerState x) => x.Filter);
        d.RegisterField("TargetSeconds", (ImagingLayerState x) => x.TargetSeconds);
        d.RegisterField("MinSeconds", (ImagingLayerState x) => x.MinSeconds);
        d.RegisterField("MeanSeconds", (ImagingLayerState x) => x.MeanSeconds);
        d.RegisterField("Scopes", (ImagingLayerState x) => x.Scopes).Description("how many scopes have a camera that feeds it");
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
    public BinaryConvertibleCollection<ImagingLayerState> Layers { get; set; } = new();

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
        d.RegisterField("Layers", (ImagingState x) => x.Layers, maxCount: 8).Description("how far each layer has got (with several layers the Map shows the least advanced one at each spot)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>An image that was started and kept: it can be carried on.</summary>
public class SavedImage : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public ImagingRequest Request { get; set; } = new();
    public BinaryConvertibleDouble MinSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MeanSeconds { get; set; } = 0.0;
    public BinaryConvertibleInt32 Visits { get; set; } = 0;
    public BinaryConvertibleString UpdatedUtc { get; set; } = "";

    public override string Name => "SavedImage";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SavedImage()
    {
        d.RegisterField("Label", (SavedImage x) => x.Label);
        d.RegisterField("Request", (SavedImage x) => x.Request).Description("as it was last started");
        d.RegisterField("MinSeconds", (SavedImage x) => x.MinSeconds).Description("least exposed spot so far");
        d.RegisterField("MeanSeconds", (SavedImage x) => x.MeanSeconds);
        d.RegisterField("Visits", (SavedImage x) => x.Visits).Description("good shots so far, all nights");
        d.RegisterField("UpdatedUtc", (SavedImage x) => x.UpdatedUtc);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SavedImages : IBinaryConvertible
{
    public BinaryConvertibleCollection<SavedImage> Images { get; set; } = new();

    public override string Name => "SavedImages";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SavedImages() { d.RegisterField("Images", (SavedImages x) => x.Images, maxCount: 200); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ImagingIds
{
    /// <summary>Void in, SavedImages out: the images that can be carried on.</summary>
    public const string ListSaved = Root + ".ListSaved";
    /// <summary>BinaryConvertibleString (label) in: forget a kept image (its coverage; the stack is kept separately).</summary>
    public const string DeleteSaved = Root + ".DeleteSaved";
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
