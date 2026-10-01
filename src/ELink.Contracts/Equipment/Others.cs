using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Focuser. Commands under ELink.Focuser.&lt;Id&gt;.: Connect(bool), MoveTo(int32 position), MoveBy(int32 steps), Abort(Void).</summary>
public class FocuserState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleInt32 Position { get; set; } = 0;
    public BinaryConvertibleInt32 MaxPosition { get; set; } = 0;
    public BinaryConvertibleBool Moving { get; set; } = false;
    public BinaryConvertibleDouble Temperature { get; set; } = double.NaN;
    public BinaryConvertibleBool CanMoveAbsolute { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "FocuserState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocuserState()
    {
        d.RegisterField("Connected", (FocuserState x) => x.Connected);
        d.RegisterField("Position", (FocuserState x) => x.Position).Description("absolute position in steps");
        d.RegisterField("MaxPosition", (FocuserState x) => x.MaxPosition);
        d.RegisterField("Moving", (FocuserState x) => x.Moving);
        d.RegisterField("Temperature", (FocuserState x) => x.Temperature).Description("°C, NaN if unavailable");
        d.RegisterField("CanMoveAbsolute", (FocuserState x) => x.CanMoveAbsolute);
        d.RegisterField("Message", (FocuserState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Filter wheel. Commands under ELink.FilterWheel.&lt;Id&gt;.: Connect(bool), SelectSlot(int32, 1-based), SelectFilter(string name).</summary>
public class FilterWheelState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleInt32 Slot { get; set; } = 0;
    public BinaryConvertibleBool Moving { get; set; } = false;
    public BinaryConvertibleCollection<BinaryConvertibleString> FilterNames { get; set; } = new();
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "FilterWheelState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FilterWheelState()
    {
        d.RegisterField("Connected", (FilterWheelState x) => x.Connected);
        d.RegisterField("Slot", (FilterWheelState x) => x.Slot).Description("current slot, 1-based");
        d.RegisterField("Moving", (FilterWheelState x) => x.Moving);
        d.RegisterField("FilterNames", (FilterWheelState x) => x.FilterNames, maxCount: 64).Description("name of slot 1, 2, ...");
        d.RegisterField("Message", (FilterWheelState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Rotator. Commands under ELink.Rotator.&lt;Id&gt;.: Connect(bool), MoveTo(double degrees), Sync(double degrees), Abort(Void).</summary>
public class RotatorState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleDouble AngleDegrees { get; set; } = 0.0;
    public BinaryConvertibleBool Moving { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "RotatorState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static RotatorState()
    {
        d.RegisterField("Connected", (RotatorState x) => x.Connected);
        d.RegisterField("AngleDegrees", (RotatorState x) => x.AngleDegrees);
        d.RegisterField("Moving", (RotatorState x) => x.Moving);
        d.RegisterField("Message", (RotatorState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
