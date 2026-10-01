using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>One job of a plan: point at a target and take N exposures.</summary>
public class SequenceBlock : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public SkyTarget Target { get; set; } = new();
    public ShooterExposure Exposure { get; set; } = new();
    public BinaryConvertibleInt32 Count { get; set; } = 1;
    public BinaryConvertibleBool RefocusBefore { get; set; } = false;

    public override string Name => "SequenceBlock";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SequenceBlock()
    {
        d.RegisterField("Label", (SequenceBlock x) => x.Label).Description("shown in the sequencer state");
        d.RegisterField("Target", (SequenceBlock x) => x.Target);
        d.RegisterField("Exposure", (SequenceBlock x) => x.Exposure);
        d.RegisterField("Count", (SequenceBlock x) => x.Count).Range(1, 100000);
        d.RegisterField("RefocusBefore", (SequenceBlock x) => x.RefocusBefore).Description("run the plan's autofocus before this block");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A list of blocks to run on one smart scope, optionally guarded by a weather station and with autofocus.</summary>
public class SequencePlan : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString ScopeId { get; set; } = "";
    public BinaryConvertibleString WeatherId { get; set; } = "";
    public AutofocusRequest Autofocus { get; set; } = new();
    public BinaryConvertibleCollection<SequenceBlock> Blocks { get; set; } = new();

    public override string Name => "SequencePlan";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SequencePlan()
    {
        d.RegisterField("Id", (SequencePlan x) => x.Id);
        d.RegisterField("ScopeId", (SequencePlan x) => x.ScopeId).Description("the smart scope that runs the blocks");
        d.RegisterField("WeatherId", (SequencePlan x) => x.WeatherId).Description("Weather device id; while it reports Unsafe the plan stops and waits. Empty = no weather guard");
        d.RegisterField("Autofocus", (SequencePlan x) => x.Autofocus).Description("used for blocks with RefocusBefore; ShooterId and FocuserId must be set");
        d.RegisterField("Blocks", (SequencePlan x) => x.Blocks, maxCount: 1000);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SequencerState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString PlanId { get; set; } = "";
    public BinaryConvertibleInt32 BlockIndex { get; set; } = 0;
    public BinaryConvertibleInt32 BlockCount { get; set; } = 0;
    public BinaryConvertibleString BlockLabel { get; set; } = "";
    public BinaryConvertibleInt32 ShotsInBlock { get; set; } = 0;
    public BinaryConvertibleInt32 ShotsPlannedInBlock { get; set; } = 0;
    public BinaryConvertibleInt32 ShotsTotal { get; set; } = 0;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "SequencerState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SequencerState()
    {
        d.RegisterField("Phase", (SequencerState x) => x.Phase).Description("Idle | Running | Focusing | WaitingForWeather | Paused | Done | Aborted | Error");
        d.RegisterField("PlanId", (SequencerState x) => x.PlanId);
        d.RegisterField("BlockIndex", (SequencerState x) => x.BlockIndex).Description("1-based index of the block in progress, 0 when idle");
        d.RegisterField("BlockCount", (SequencerState x) => x.BlockCount);
        d.RegisterField("BlockLabel", (SequencerState x) => x.BlockLabel);
        d.RegisterField("ShotsInBlock", (SequencerState x) => x.ShotsInBlock).Description("exposure rounds finished in this block");
        d.RegisterField("ShotsPlannedInBlock", (SequencerState x) => x.ShotsPlannedInBlock);
        d.RegisterField("ShotsTotal", (SequencerState x) => x.ShotsTotal).Description("exposure rounds finished in the whole plan");
        d.RegisterField("Message", (SequencerState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class SequencerIds
{
    public const string Root = "ELink.Automation.Sequencer";
    /// <summary>SequencePlan in, CommandResult out (accepted; progress arrives as state events).</summary>
    public const string Start = Root + ".Start";
    public const string Pause = Root + ".Pause";
    public const string Resume = Root + ".Resume";
    public const string Abort = Root + ".Abort";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
