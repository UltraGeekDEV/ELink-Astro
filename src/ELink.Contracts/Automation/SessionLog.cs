using System.Globalization;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>One line of the session log: something that happened, or a note from the person at the telescope.</summary>
public class LogEntry : IBinaryConvertible
{
    public BinaryConvertibleString TimeUtc { get; set; } = "";
    public BinaryConvertibleString Source { get; set; } = "";
    public BinaryConvertibleString Level { get; set; } = "info";
    public BinaryConvertibleString Text { get; set; } = "";

    public override string Name => "LogEntry";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LogEntry()
    {
        d.RegisterField("TimeUtc", (LogEntry x) => x.TimeUtc).Description("ISO 8601 UTC; empty = the log stamps it on arrival");
        d.RegisterField("Source", (LogEntry x) => x.Source).Description("who says it: a scope, a service, or 'you'");
        d.RegisterField("Level", (LogEntry x) => x.Level).Description("info | warn | error | note (a remark from the observer)");
        d.RegisterField("Text", (LogEntry x) => x.Text);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>The end of tonight's log: the file it is in and its last lines.</summary>
public class LogTail : IBinaryConvertible
{
    public BinaryConvertibleString File { get; set; } = "";
    public BinaryConvertibleString Text { get; set; } = "";

    public override string Name => "LogTail";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static LogTail()
    {
        d.RegisterField("File", (LogTail x) => x.File).Description("path of tonight's log file on the station");
        d.RegisterField("Text", (LogTail x) => x.Text).Description("the last lines, oldest first");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>The session log. Anything can publish a <see cref="LogEntry"/> on <see cref="Entry"/> (fire and forget); the log service
/// writes it to a file per night and shows it again to whoever joins. The files are also served by the log's file endpoint
/// (<see cref="FileRoot"/> + GetFile / AppendFile).</summary>
public static class LogIds
{
    public const string Root = "ELink.Log";
    /// <summary>Event: <see cref="LogEntry"/>. Published by anything that has something to say; the observer's notes use Level "note".</summary>
    public const string Entry = Root + ".Entry";
    /// <summary>BinaryConvertibleInt32 (number of lines) in, <see cref="LogTail"/> out.</summary>
    public const string Tail = Root + ".Tail";
    /// <summary>The file endpoint's ID prefix: ELink.Log.GetFile (path in, FileContent out), ELink.Log.AppendFile.</summary>
    public const string FileRoot = Root + ".";
}

/// <summary>How a <see cref="LogEntry"/> is written in the file and shown on the page.</summary>
public static class LogFormat
{
    public static string Line(LogEntry e)
    {
        var when = DateTime.TryParse(e.TimeUtc.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : DateTime.Now;
        var text = e.Text.Text.Replace("\r", "").Replace("\n", " ⏎ ");
        return $"{when:yyyy-MM-dd HH:mm:ss.fff} {e.Level.Text,-5} {(e.Source.Text == "" ? "-" : e.Source.Text)}: {text}";
    }
}
