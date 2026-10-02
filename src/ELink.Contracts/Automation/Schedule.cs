using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>One image in the schedule, with when it may be taken.</summary>
public class ScheduleEntry : IBinaryConvertible
{
    public ImagingRequest Request { get; set; } = new();
    public BinaryConvertibleInt32 Priority { get; set; } = 0;
    public BinaryConvertibleBool Enabled { get; set; } = true;
    public BinaryConvertibleDouble MinAltitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleBool RequireDark { get; set; } = true;
    public BinaryConvertibleDouble MinMoonSeparationDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble MaxMoonIllumination { get; set; } = 1.0;
    public BinaryConvertibleString NotBeforeUtc { get; set; } = "";
    public BinaryConvertibleString NotAfterUtc { get; set; } = "";

    public override string Name => "ScheduleEntry";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScheduleEntry()
    {
        d.RegisterField("Request", (ScheduleEntry x) => x.Request).Description("the image; its Label is the entry's name (and what makes it carry on across nights)");
        d.RegisterField("Priority", (ScheduleEntry x) => x.Priority).Description("higher first; among equals the one that sets soonest");
        d.RegisterField("Enabled", (ScheduleEntry x) => x.Enabled);
        d.RegisterField("MinAltitudeDegrees", (ScheduleEntry x) => x.MinAltitudeDegrees).Description("NaN = the site's horizon");
        d.RegisterField("RequireDark", (ScheduleEntry x) => x.RequireDark).Description("only while the Sun is 18 degrees down");
        d.RegisterField("MinMoonSeparationDegrees", (ScheduleEntry x) => x.MinMoonSeparationDegrees).Description("0 = no limit");
        d.RegisterField("MaxMoonIllumination", (ScheduleEntry x) => x.MaxMoonIllumination).Description("when the Moon is up: at most this lit (0..1); 1 = no limit");
        d.RegisterField("NotBeforeUtc", (ScheduleEntry x) => x.NotBeforeUtc).Description("ISO 8601; empty = any time");
        d.RegisterField("NotAfterUtc", (ScheduleEntry x) => x.NotAfterUtc);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class Schedule : IBinaryConvertible
{
    public BinaryConvertibleCollection<ScheduleEntry> Entries { get; set; } = new();
    public BinaryConvertibleDouble MinRunMinutes { get; set; } = 30.0;

    public override string Name => "Schedule";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static Schedule()
    {
        d.RegisterField("Entries", (Schedule x) => x.Entries, maxCount: 200);
        d.RegisterField("MinRunMinutes", (Schedule x) => x.MinRunMinutes).Description("an entry is only started if it stays possible at least this long");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ScheduleEntryState : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Status { get; set; } = "";
    public BinaryConvertibleString Reason { get; set; } = "";
    public BinaryConvertibleDouble CompletePercent { get; set; } = 0.0;
    public BinaryConvertibleDouble Altitude { get; set; } = double.NaN;

    public override string Name => "ScheduleEntryState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScheduleEntryState()
    {
        d.RegisterField("Label", (ScheduleEntryState x) => x.Label);
        d.RegisterField("Status", (ScheduleEntryState x) => x.Status).Description("Running | Possible | Waiting | Complete | Disabled");
        d.RegisterField("Reason", (ScheduleEntryState x) => x.Reason).Description("why it waits (too low, not dark, the Moon, ...)");
        d.RegisterField("CompletePercent", (ScheduleEntryState x) => x.CompletePercent).Description("least exposed spot against the target, all nights so far");
        d.RegisterField("Altitude", (ScheduleEntryState x) => x.Altitude).Description("of the image's centre, now");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SchedulerState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Stopped";
    public BinaryConvertibleString Running { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleCollection<ScheduleEntryState> Entries { get; set; } = new();

    public override string Name => "SchedulerState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SchedulerState()
    {
        d.RegisterField("Phase", (SchedulerState x) => x.Phase).Description("Stopped | Imaging | Waiting | AllDone");
        d.RegisterField("Running", (SchedulerState x) => x.Running).Description("label of the image being taken");
        d.RegisterField("Message", (SchedulerState x) => x.Message);
        d.RegisterField("Entries", (SchedulerState x) => x.Entries, maxCount: 200);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class SchedulerIds
{
    public const string Root = "ELink.Automation.Scheduler";
    /// <summary>Schedule in, CommandResult out: replace the schedule (kept on disk).</summary>
    public const string SetSchedule = Root + ".SetSchedule";
    /// <summary>Void in, Schedule out.</summary>
    public const string GetSchedule = Root + ".GetSchedule";
    /// <summary>Void in: run the schedule (and keep running it, night after night, until stopped).</summary>
    public const string Start = Root + ".Start";
    /// <summary>Void in: stop (the image being taken is stopped too, and kept).</summary>
    public const string Stop = Root + ".Stop";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
}
