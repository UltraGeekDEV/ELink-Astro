using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Focus a Shooter by moving a Focuser through a series of positions and measuring star sharpness (HFR).</summary>
public class AutofocusRequest : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString FocuserId { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 3.0;
    public BinaryConvertibleInt32 StepSize { get; set; } = 3000;
    public BinaryConvertibleInt32 Samples { get; set; } = 7;
    public BinaryConvertibleString Filter { get; set; } = "";

    public override string Name => "AutofocusRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AutofocusRequest()
    {
        d.RegisterField("ShooterId", (AutofocusRequest x) => x.ShooterId).Description("Shooter that takes the test frames");
        d.RegisterField("FocuserId", (AutofocusRequest x) => x.FocuserId).Description("Focuser (Equipment id) to move");
        d.RegisterField("ExposureSeconds", (AutofocusRequest x) => x.ExposureSeconds);
        d.RegisterField("StepSize", (AutofocusRequest x) => x.StepSize).Description("focuser steps between samples");
        d.RegisterField("Samples", (AutofocusRequest x) => x.Samples).Description("measurements per sweep, 5..15").Range(5, 15);
        d.RegisterField("Filter", (AutofocusRequest x) => x.Filter).Description("filter to focus through, empty = as is");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class FocusPoint : IBinaryConvertible
{
    public BinaryConvertibleInt32 Position { get; set; } = 0;
    public BinaryConvertibleDouble Hfr { get; set; } = double.NaN;
    public BinaryConvertibleInt32 Stars { get; set; } = 0;

    public override string Name => "FocusPoint";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusPoint()
    {
        d.RegisterField("Position", (FocusPoint x) => x.Position);
        d.RegisterField("Hfr", (FocusPoint x) => x.Hfr).Description("median half-flux radius in pixels, NaN if no stars");
        d.RegisterField("Stars", (FocusPoint x) => x.Stars);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Service endpoints (one run at a time): <see cref="AutofocusIds.Run"/>, <see cref="AutofocusIds.Abort"/>,
/// state event <see cref="AutofocusIds.State"/> and <see cref="AutofocusIds.GetState"/>.</summary>
public class AutofocusState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Round { get; set; } = 0;
    public BinaryConvertibleInt32 BestPosition { get; set; } = 0;
    public BinaryConvertibleDouble BestHfr { get; set; } = double.NaN;
    public BinaryConvertibleCollection<FocusPoint> Points { get; set; } = new();

    public override string Name => "AutofocusState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AutofocusState()
    {
        d.RegisterField("Phase", (AutofocusState x) => x.Phase).Description("Idle | Moving | Exposing | Fitting | Done | Error | Aborted");
        d.RegisterField("Message", (AutofocusState x) => x.Message);
        d.RegisterField("Round", (AutofocusState x) => x.Round).Description("sweep number, 1-based");
        d.RegisterField("BestPosition", (AutofocusState x) => x.BestPosition).Description("valid in Done");
        d.RegisterField("BestHfr", (AutofocusState x) => x.BestHfr);
        d.RegisterField("Points", (AutofocusState x) => x.Points, maxCount: 256).Description("every measurement of this run");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class AutofocusIds
{
    public const string Root = "ELink.Automation.Autofocus";
    /// <summary>AutofocusRequest in, CommandResult out (accepted; progress arrives as state events).</summary>
    public const string Run = Root + ".Run";
    public const string Abort = Root + ".Abort";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
