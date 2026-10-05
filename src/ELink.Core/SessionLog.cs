using ELink.Contracts.Automation;
using Event.CoreFunctionality;

namespace ELink.Core;

/// <summary>Writing to the session log from anywhere: one fire-and-forget event, never an error for the caller.</summary>
public static class SessionLog
{
    public static void Info(TypeSafeEVentNode node, string source, string text) => Write(node, source, "info", text);
    public static void Warn(TypeSafeEVentNode node, string source, string text) => Write(node, source, "warn", text);
    public static void Error(TypeSafeEVentNode node, string source, string text) => Write(node, source, "error", text);
    public static void Note(TypeSafeEVentNode node, string text) => Write(node, "you", "note", text);

    public static void Write(TypeSafeEVentNode node, string source, string level, string text)
    {
        var entry = new LogEntry { TimeUtc = DateTime.UtcNow.ToString("o"), Source = source, Level = level, Text = text };
        _ = Task.Run(async () => { try { await node.PublishEventAsync(LogIds.Entry, entry); } catch (Exception) { } });
    }
}
