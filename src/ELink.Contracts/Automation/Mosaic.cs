using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>The footprint of one frame a scope takes on every visit: its size, how it is rotated on the scope, and where
/// it looks relative to the point the scope centres. Heterogeneous frames (other sizes, other angles) are just more
/// of these.</summary>
public class MosaicFrame : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble WidthDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble HeightDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble RotationDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetEastDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetNorthDegrees { get; set; } = 0.0;

    public override string Name => "MosaicFrame";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicFrame()
    {
        d.RegisterField("Label", (MosaicFrame x) => x.Label);
        d.RegisterField("WidthDegrees", (MosaicFrame x) => x.WidthDegrees).Description("field of view along the frame's x axis");
        d.RegisterField("HeightDegrees", (MosaicFrame x) => x.HeightDegrees).Description("field of view along the frame's y axis");
        d.RegisterField("RotationDegrees", (MosaicFrame x) => x.RotationDegrees).Description("how the frame is turned on the scope (position angle, north through east)");
        d.RegisterField("OffsetEastDegrees", (MosaicFrame x) => x.OffsetEastDegrees).Description("where its centre is, along the scope's own east axis, from the point the scope centres");
        d.RegisterField("OffsetNorthDegrees", (MosaicFrame x) => x.OffsetNorthDegrees).Description("same, along the scope's north axis");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Slowly paint a virtual field of view with exposure time: many single shots in small stepovers, always toward the
/// least-covered part, until every spot has received TargetSeconds in total.</summary>
public class MosaicRequest : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "Mosaic";
    public BinaryConvertibleString ScopeId { get; set; } = "";
    public SkyTarget Center { get; set; } = new();
    public BinaryConvertibleDouble FovWidthDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble FovHeightDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble PositionAngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleCollection<MosaicFrame> Frames { get; set; } = new();
    public BinaryConvertibleDouble StepoverDegrees { get; set; } = 0.0;
    public ShooterExposure Exposure { get; set; } = new();
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleInt32 MaxVisits { get; set; } = 0;
    public BinaryConvertibleDouble CellDegrees { get; set; } = 0.0;
    public BinaryConvertibleString RotatorId { get; set; } = "";
    public BinaryConvertibleDouble RotatorOffsetDegrees { get; set; } = 0.0;
    public BinaryConvertibleCollection<BinaryConvertibleDouble> FieldRotations { get; set; } = new();
    public BinaryConvertibleString WeatherId { get; set; } = "";
    public BinaryConvertibleDouble SlewTimeoutSeconds { get; set; } = 300.0;

    public override string Name => "MosaicRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicRequest()
    {
        d.RegisterField("Label", (MosaicRequest x) => x.Label).Description("names the run; frames are called <Label>_v<visit>");
        d.RegisterField("ScopeId", (MosaicRequest x) => x.ScopeId).Description("the smart scope that points and shoots; one visit = one shot of every shooter");
        d.RegisterField("Center", (MosaicRequest x) => x.Center).Description("centre of the virtual field of view, J2000");
        d.RegisterField("FovWidthDegrees", (MosaicRequest x) => x.FovWidthDegrees).Description("width of the area to paint, along its own x axis");
        d.RegisterField("FovHeightDegrees", (MosaicRequest x) => x.FovHeightDegrees);
        d.RegisterField("PositionAngleDegrees", (MosaicRequest x) => x.PositionAngleDegrees).Description("rotation of the area on the sky, north through east");
        d.RegisterField("Frames", (MosaicRequest x) => x.Frames, maxCount: 64).Description("footprints of what the scope shoots on each visit");
        d.RegisterField("StepoverDegrees", (MosaicRequest x) => x.StepoverDegrees).Description("largest hop between consecutive shots; 0 = a tenth of the smallest frame");
        d.RegisterField("Exposure", (MosaicRequest x) => x.Exposure).Description("every visit is one shot of this");
        d.RegisterField("TargetSeconds", (MosaicRequest x) => x.TargetSeconds).Description("exposure time every spot of the area should receive in total (summed over frames); 0 = paint until aborted");
        d.RegisterField("MaxVisits", (MosaicRequest x) => x.MaxVisits).Description("stop after this many shots; 0 = no limit");
        d.RegisterField("CellDegrees", (MosaicRequest x) => x.CellDegrees).Description("resolution of the coverage map; 0 = automatic");
        d.RegisterField("RotatorId", (MosaicRequest x) => x.RotatorId).Description("Rotator device id to turn the whole scope; empty = no rotator");
        d.RegisterField("RotatorOffsetDegrees", (MosaicRequest x) => x.RotatorOffsetDegrees).Description("sky position angle = rotator angle + this");
        d.RegisterField("FieldRotations", (MosaicRequest x) => x.FieldRotations, maxCount: 64).Description("position angles the scope may use (needs a rotator); the planner picks one per visit");
        d.RegisterField("WeatherId", (MosaicRequest x) => x.WeatherId).Description("Weather device id: hold while it reports Unsafe");
        d.RegisterField("SlewTimeoutSeconds", (MosaicRequest x) => x.SlewTimeoutSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>What a request amounts to (answer of <see cref="MosaicIds.Plan"/>), without running anything.</summary>
public class MosaicPreview : IBinaryConvertible
{
    public BinaryConvertibleString Error { get; set; } = "";
    public BinaryConvertibleDouble StepoverDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble CellDegrees { get; set; } = 0.0;
    public BinaryConvertibleInt32 MapCols { get; set; } = 0;
    public BinaryConvertibleInt32 MapRows { get; set; } = 0;
    public BinaryConvertibleInt32 EstimatedVisits { get; set; } = 0;
    public BinaryConvertibleDouble EstimatedHours { get; set; } = 0.0;

    public override string Name => "MosaicPreview";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicPreview()
    {
        d.RegisterField("Error", (MosaicPreview x) => x.Error).Description("empty when the request is valid");
        d.RegisterField("StepoverDegrees", (MosaicPreview x) => x.StepoverDegrees).Description("the stepover that will be used");
        d.RegisterField("CellDegrees", (MosaicPreview x) => x.CellDegrees);
        d.RegisterField("MapCols", (MosaicPreview x) => x.MapCols);
        d.RegisterField("MapRows", (MosaicPreview x) => x.MapRows);
        d.RegisterField("EstimatedVisits", (MosaicPreview x) => x.EstimatedVisits).Description("shots needed to reach TargetSeconds, roughly; 0 if there is no target");
        d.RegisterField("EstimatedHours", (MosaicPreview x) => x.EstimatedHours).Description("assuming a few seconds of slewing and download per shot");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class MosaicState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Visits { get; set; } = 0;
    public BinaryConvertibleInt32 QueueDepth { get; set; } = 0;
    public BinaryConvertibleDouble TargetSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble StepoverDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble PassHopDegrees { get; set; } = double.NaN;
    public BinaryConvertibleInt32 Pass { get; set; } = 0;
    public BinaryConvertibleInt32 Passes { get; set; } = 0;
    public BinaryConvertibleDouble MinSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MeanSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble MaxSeconds { get; set; } = 0.0;
    public BinaryConvertibleDouble PoseX { get; set; } = double.NaN;
    public BinaryConvertibleDouble PoseY { get; set; } = double.NaN;
    public BinaryConvertibleDouble PoseFieldAngle { get; set; } = 0.0;
    public BinaryConvertibleInt32 MapCols { get; set; } = 0;
    public BinaryConvertibleInt32 MapRows { get; set; } = 0;
    public RawBytes Map { get; set; } = new();

    public override string Name => "MosaicState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicState()
    {
        d.RegisterField("Phase", (MosaicState x) => x.Phase).Description("Idle | Running | Paused | WaitingForWeather | Done | Aborted | Error");
        d.RegisterField("Label", (MosaicState x) => x.Label);
        d.RegisterField("Message", (MosaicState x) => x.Message);
        d.RegisterField("Visits", (MosaicState x) => x.Visits).Description("single shots completed");
        d.RegisterField("QueueDepth", (MosaicState x) => x.QueueDepth).Description("visits planned ahead and waiting for the executor");
        d.RegisterField("TargetSeconds", (MosaicState x) => x.TargetSeconds);
        d.RegisterField("StepoverDegrees", (MosaicState x) => x.StepoverDegrees).Description("the largest hop that was asked for");
        d.RegisterField("PassHopDegrees", (MosaicState x) => x.PassHopDegrees).Description("the hop between shots actually in use: at least the stepover, larger when a light pass needs it to deposit the right depth");
        d.RegisterField("Pass", (MosaicState x) => x.Pass).Description("raster pass in progress, 1-based; 0 while topping up");
        d.RegisterField("Passes", (MosaicState x) => x.Passes).Description("raster passes planned for the target");
        d.RegisterField("MinSeconds", (MosaicState x) => x.MinSeconds).Description("least-covered spot of the area: this is what reaching the target means");
        d.RegisterField("MeanSeconds", (MosaicState x) => x.MeanSeconds);
        d.RegisterField("MaxSeconds", (MosaicState x) => x.MaxSeconds);
        d.RegisterField("PoseX", (MosaicState x) => x.PoseX).Description("where the scope points, in the area's own frame (degrees from its centre, x along the width); NaN before the first shot");
        d.RegisterField("PoseY", (MosaicState x) => x.PoseY);
        d.RegisterField("PoseFieldAngle", (MosaicState x) => x.PoseFieldAngle).Description("field rotation of the scope, position angle");
        d.RegisterField("MapCols", (MosaicState x) => x.MapCols).Description("coverage map size; row 0 is the top (north edge of the area), column 0 the left");
        d.RegisterField("MapRows", (MosaicState x) => x.MapRows);
        d.RegisterField("Map", (MosaicState x) => x.Map).Description("one byte per cell: exposure time scaled so 255 = max(target, the most-covered spot)");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class MosaicIds
{
    public const string Root = "ELink.Automation.Mosaic";
    /// <summary>MosaicRequest in, MosaicPreview out: validate and size a request without running anything.</summary>
    public const string Plan = Root + ".Plan";
    /// <summary>MosaicRequest in, CommandResult out (accepted; progress arrives as state events).</summary>
    public const string Start = Root + ".Start";
    public const string Pause = Root + ".Pause";
    public const string Resume = Root + ".Resume";
    public const string Abort = Root + ".Abort";
    /// <summary>BinaryConvertibleDouble (seconds, 0 = until aborted) in, CommandResult out. Takes effect while running.</summary>
    public const string SetTarget = Root + ".SetTarget";
    /// <summary>BinaryConvertibleDouble (degrees) in, CommandResult out. Takes effect while running.</summary>
    public const string SetStepover = Root + ".SetStepover";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
