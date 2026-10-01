using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Focuser, as the standard INDI focuser interface describes it (the properties Ekos drives): absolute, relative
/// and timed moves, abort, sync, reverse, backlash, max travel, speed and temperature. What a given focuser can do is in
/// the Can*/Has* flags. Commands under ELink.Focuser.&lt;Id&gt;.: Connect(bool), MoveTo(int32), MoveBy(int32, negative =
/// inward), MoveTimed(<see cref="FocusTimedMove"/>), Abort(Void), Sync(int32), SetReverse(bool),
/// SetBacklash(<see cref="FocusBacklash"/>), SetMaxPosition(int32), SetSpeed(double), GetState(Void).</summary>
public class FocuserState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleInt32 Position { get; set; } = 0;
    public BinaryConvertibleInt32 MaxPosition { get; set; } = 0;
    public BinaryConvertibleBool Moving { get; set; } = false;
    public BinaryConvertibleDouble Temperature { get; set; } = double.NaN;
    public BinaryConvertibleBool CanMoveAbsolute { get; set; } = false;
    public BinaryConvertibleBool CanMoveRelative { get; set; } = false;
    public BinaryConvertibleBool CanMoveTimed { get; set; } = false;
    public BinaryConvertibleBool CanAbort { get; set; } = false;
    public BinaryConvertibleBool CanSync { get; set; } = false;
    public BinaryConvertibleBool CanReverse { get; set; } = false;
    public BinaryConvertibleBool Reversed { get; set; } = false;
    public BinaryConvertibleBool HasBacklash { get; set; } = false;
    public BinaryConvertibleBool BacklashEnabled { get; set; } = false;
    public BinaryConvertibleInt32 BacklashSteps { get; set; } = 0;
    public BinaryConvertibleDouble Speed { get; set; } = double.NaN;
    public BinaryConvertibleDouble SpeedMax { get; set; } = double.NaN;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "FocuserState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocuserState()
    {
        d.RegisterField("Connected", (FocuserState x) => x.Connected);
        d.RegisterField("Position", (FocuserState x) => x.Position).Description("absolute position in steps (0 for focusers that do not know it)");
        d.RegisterField("MaxPosition", (FocuserState x) => x.MaxPosition).Description("maximum travel in steps, 0 if unknown");
        d.RegisterField("Moving", (FocuserState x) => x.Moving);
        d.RegisterField("Temperature", (FocuserState x) => x.Temperature).Description("°C, NaN if the focuser has no sensor");
        d.RegisterField("CanMoveAbsolute", (FocuserState x) => x.CanMoveAbsolute);
        d.RegisterField("CanMoveRelative", (FocuserState x) => x.CanMoveRelative);
        d.RegisterField("CanMoveTimed", (FocuserState x) => x.CanMoveTimed).Description("DC focusers: move for a time in a direction");
        d.RegisterField("CanAbort", (FocuserState x) => x.CanAbort);
        d.RegisterField("CanSync", (FocuserState x) => x.CanSync).Description("the current position can be redefined without moving");
        d.RegisterField("CanReverse", (FocuserState x) => x.CanReverse);
        d.RegisterField("Reversed", (FocuserState x) => x.Reversed);
        d.RegisterField("HasBacklash", (FocuserState x) => x.HasBacklash).Description("the driver compensates backlash");
        d.RegisterField("BacklashEnabled", (FocuserState x) => x.BacklashEnabled);
        d.RegisterField("BacklashSteps", (FocuserState x) => x.BacklashSteps);
        d.RegisterField("Speed", (FocuserState x) => x.Speed).Description("NaN if the speed cannot be set");
        d.RegisterField("SpeedMax", (FocuserState x) => x.SpeedMax);
        d.RegisterField("Message", (FocuserState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Move a (typically DC) focuser in a direction for a time.</summary>
public class FocusTimedMove : IBinaryConvertible
{
    public BinaryConvertibleBool Outward { get; set; } = true;
    public BinaryConvertibleInt32 Milliseconds { get; set; } = 500;

    public override string Name => "FocusTimedMove";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusTimedMove()
    {
        d.RegisterField("Outward", (FocusTimedMove x) => x.Outward).Description("true = outward, false = inward");
        d.RegisterField("Milliseconds", (FocusTimedMove x) => x.Milliseconds).Range(1, 60000);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class FocusBacklash : IBinaryConvertible
{
    public BinaryConvertibleBool Enabled { get; set; } = true;
    public BinaryConvertibleInt32 Steps { get; set; } = 0;

    public override string Name => "FocusBacklash";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static FocusBacklash()
    {
        d.RegisterField("Enabled", (FocusBacklash x) => x.Enabled);
        d.RegisterField("Steps", (FocusBacklash x) => x.Steps).Description("compensation in steps; ignored when Enabled is false");
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
