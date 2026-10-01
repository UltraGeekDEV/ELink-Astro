using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>A virtual field of view to cover with many single shots, panel after panel, round after round.</summary>
public class MosaicRequest : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "Mosaic";
    public BinaryConvertibleString ScopeId { get; set; } = "";
    public SkyTarget Center { get; set; } = new();
    public BinaryConvertibleDouble FovWidthDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble FovHeightDegrees { get; set; } = 1.0;
    public BinaryConvertibleDouble PositionAngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble FrameWidthDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble FrameHeightDegrees { get; set; } = 0.5;
    public BinaryConvertibleDouble Overlap { get; set; } = 0.2;
    public ShooterExposure Exposure { get; set; } = new();
    public BinaryConvertibleInt32 Passes { get; set; } = 1;
    public BinaryConvertibleString WeatherId { get; set; } = "";
    public BinaryConvertibleDouble SlewTimeoutSeconds { get; set; } = 300.0;

    public override string Name => "MosaicRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicRequest()
    {
        d.RegisterField("Label", (MosaicRequest x) => x.Label).Description("names the run; frames are called <Label>_r<row>c<col>");
        d.RegisterField("ScopeId", (MosaicRequest x) => x.ScopeId).Description("the smart scope that points and shoots");
        d.RegisterField("Center", (MosaicRequest x) => x.Center).Description("centre of the virtual field of view");
        d.RegisterField("FovWidthDegrees", (MosaicRequest x) => x.FovWidthDegrees).Description("width of the area to cover, on the sky").Range(0.001m, 90m);
        d.RegisterField("FovHeightDegrees", (MosaicRequest x) => x.FovHeightDegrees).Description("height of the area to cover, on the sky").Range(0.001m, 90m);
        d.RegisterField("PositionAngleDegrees", (MosaicRequest x) => x.PositionAngleDegrees).Description("rotation of the area, north through east");
        d.RegisterField("FrameWidthDegrees", (MosaicRequest x) => x.FrameWidthDegrees).Description("field of view of one shot");
        d.RegisterField("FrameHeightDegrees", (MosaicRequest x) => x.FrameHeightDegrees).Description("field of view of one shot");
        d.RegisterField("Overlap", (MosaicRequest x) => x.Overlap).Description("fraction of a frame that neighbouring panels share").Range(0m, 0.9m);
        d.RegisterField("Exposure", (MosaicRequest x) => x.Exposure).Description("one single shot, taken at every visit");
        d.RegisterField("Passes", (MosaicRequest x) => x.Passes).Description("rounds over the whole area; 0 = keep going until aborted (changeable while running)");
        d.RegisterField("WeatherId", (MosaicRequest x) => x.WeatherId).Description("Weather device id: hold while it reports Unsafe. Empty = none");
        d.RegisterField("SlewTimeoutSeconds", (MosaicRequest x) => x.SlewTimeoutSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class MosaicPanel : IBinaryConvertible
{
    public BinaryConvertibleInt32 Row { get; set; } = 0;
    public BinaryConvertibleInt32 Col { get; set; } = 0;
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleBool Skipped { get; set; } = false;

    public override string Name => "MosaicPanel";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicPanel()
    {
        d.RegisterField("Row", (MosaicPanel x) => x.Row).Description("0 = northernmost row of the (rotated) mosaic");
        d.RegisterField("Col", (MosaicPanel x) => x.Col).Description("0 = westernmost column; grows eastward");
        d.RegisterField("RaHours", (MosaicPanel x) => x.RaHours).Description("panel centre, J2000");
        d.RegisterField("DecDegrees", (MosaicPanel x) => x.DecDegrees).Description("panel centre, J2000");
        d.RegisterField("Frames", (MosaicPanel x) => x.Frames).Description("shots taken here so far");
        d.RegisterField("Skipped", (MosaicPanel x) => x.Skipped).Description("excluded from the scan");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>The panels a request tiles into (answer of <see cref="MosaicIds.Plan"/>).</summary>
public class MosaicLayout : IBinaryConvertible
{
    public BinaryConvertibleInt32 Rows { get; set; } = 0;
    public BinaryConvertibleInt32 Cols { get; set; } = 0;
    public BinaryConvertibleDouble StepXDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble StepYDegrees { get; set; } = 0.0;
    public BinaryConvertibleCollection<MosaicPanel> Panels { get; set; } = new();
    public BinaryConvertibleString Error { get; set; } = "";

    public override string Name => "MosaicLayout";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicLayout()
    {
        d.RegisterField("Rows", (MosaicLayout x) => x.Rows);
        d.RegisterField("Cols", (MosaicLayout x) => x.Cols);
        d.RegisterField("StepXDegrees", (MosaicLayout x) => x.StepXDegrees).Description("distance between neighbouring panel centres");
        d.RegisterField("StepYDegrees", (MosaicLayout x) => x.StepYDegrees);
        d.RegisterField("Panels", (MosaicLayout x) => x.Panels, maxCount: 4096);
        d.RegisterField("Error", (MosaicLayout x) => x.Error).Description("empty when the request is valid");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class PanelSkip : IBinaryConvertible
{
    public BinaryConvertibleInt32 Row { get; set; } = 0;
    public BinaryConvertibleInt32 Col { get; set; } = 0;
    public BinaryConvertibleBool Skip { get; set; } = true;

    public override string Name => "PanelSkip";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static PanelSkip()
    {
        d.RegisterField("Row", (PanelSkip x) => x.Row);
        d.RegisterField("Col", (PanelSkip x) => x.Col);
        d.RegisterField("Skip", (PanelSkip x) => x.Skip);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class MosaicState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Pass { get; set; } = 0;
    public BinaryConvertibleInt32 Passes { get; set; } = 0;
    public BinaryConvertibleInt32 VisitsDone { get; set; } = 0;
    public BinaryConvertibleInt32 QueueDepth { get; set; } = 0;
    public BinaryConvertibleInt32 CurrentRow { get; set; } = -1;
    public BinaryConvertibleInt32 CurrentCol { get; set; } = -1;
    public MosaicLayout Layout { get; set; } = new();

    public override string Name => "MosaicState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MosaicState()
    {
        d.RegisterField("Phase", (MosaicState x) => x.Phase).Description("Idle | Running | Paused | WaitingForWeather | Done | Aborted | Error");
        d.RegisterField("Label", (MosaicState x) => x.Label);
        d.RegisterField("Message", (MosaicState x) => x.Message);
        d.RegisterField("Pass", (MosaicState x) => x.Pass).Description("round in progress, 1-based");
        d.RegisterField("Passes", (MosaicState x) => x.Passes).Description("rounds wanted now; 0 = until aborted");
        d.RegisterField("VisitsDone", (MosaicState x) => x.VisitsDone).Description("single shots completed");
        d.RegisterField("QueueDepth", (MosaicState x) => x.QueueDepth).Description("visits planned by the producer and waiting for the executor");
        d.RegisterField("CurrentRow", (MosaicState x) => x.CurrentRow);
        d.RegisterField("CurrentCol", (MosaicState x) => x.CurrentCol);
        d.RegisterField("Layout", (MosaicState x) => x.Layout).Description("the panels with their frame counts: the coverage map");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class MosaicIds
{
    public const string Root = "ELink.Automation.Mosaic";
    /// <summary>MosaicRequest in, MosaicLayout out: what the request tiles into, without running anything.</summary>
    public const string Plan = Root + ".Plan";
    /// <summary>MosaicRequest in, CommandResult out (accepted; progress arrives as state events).</summary>
    public const string Start = Root + ".Start";
    public const string Pause = Root + ".Pause";
    public const string Resume = Root + ".Resume";
    public const string Abort = Root + ".Abort";
    /// <summary>BinaryConvertibleInt32 in (0 = until aborted), CommandResult out. Takes effect while running.</summary>
    public const string SetPasses = Root + ".SetPasses";
    /// <summary>PanelSkip in, CommandResult out. Takes effect while running.</summary>
    public const string SkipPanel = Root + ".SkipPanel";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
