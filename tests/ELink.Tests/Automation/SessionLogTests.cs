using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Core;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.Endpoints;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>The session log: lines and notes end up in one file per night, can be read back, and the file endpoint may be
/// appended to over the mesh but not overwritten.</summary>
public class SessionLogTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elink-log-" + Guid.NewGuid().ToString("N"));
    private TypeSafeEVentNode _node = null!;
    private SessionLogService _log = null!;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("LOG-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _log = new SessionLogService(_node, _dir);
        await _log.StartAsync();
    }

    public async Task DisposeAsync() { await _log.DisposeAsync(); _node.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private async Task<string> TextAsync(string expect)
    {
        string text = "";
        await UiWait(() => { text = File.Exists(_log.CurrentFile) ? File.ReadAllText(_log.CurrentFile) : ""; return text.Contains(expect); });
        return text;
    }

    private static async Task UiWait(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !cond()) await Task.Delay(30);
    }

    [Fact]
    public async Task EntriesAndNotesAreWrittenAsReadableLinesAndReadBack()
    {
        SessionLog.Warn(_node, "main", "lost the guide star");
        SessionLog.Note(_node, "clouds from the west\nsecond line");
        var text = await TextAsync("clouds from the west");
        Assert.Contains("warn  main: lost the guide star", text);
        Assert.Contains("note  you: clouds from the west ⏎ second line", text);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ", text);

        var tail = (await _node.CallFunctionAsync<BinaryConvertibleInt32, LogTail>(LogIds.Tail, 2, TimeSpan.FromSeconds(5)))!.Single();
        Assert.Equal(_log.CurrentFile, tail.File.Text);
        Assert.Contains("clouds from the west", tail.Text.Text);
        Assert.Equal(2, tail.Text.Text.Split('\n').Length);
    }

    [Fact]
    public async Task AnyNodeMayAppendToTheFileButNotReplaceIt()
    {
        SessionLog.Info(_node, "station", "first");
        await TextAsync("first");
        string name = Path.GetFileName(_log.CurrentFile);
        Assert.True(await _node.FireEventAsync(LogIds.FileRoot + FileEndpoint.AppendFileSuffix, new FileContent(name, System.Text.Encoding.UTF8.GetBytes("appended from elsewhere\n"))));
        Assert.Contains("appended from elsewhere", await TextAsync("appended from elsewhere"));

        await _node.FireEventAsync(LogIds.FileRoot + FileEndpoint.SaveFileSuffix, new FileContent(name, System.Text.Encoding.UTF8.GetBytes("wiped\n")));
        await Task.Delay(300);
        var after = File.ReadAllText(_log.CurrentFile);
        Assert.DoesNotContain("wiped", after);
        Assert.Contains("first", after);

        var got = await _node.CallFunctionAsync<BinaryConvertibleString, FileContent>(LogIds.FileRoot + FileEndpoint.GetFileSuffix, name);
        Assert.Contains("first", System.Text.Encoding.UTF8.GetString(got!.Single().Data.Value));
    }

    [Fact]
    public void TheNightKeepsItsDateUntilNoon() =>
        Assert.Equal("session-2026-10-05.log", SessionLogService.FileNameFor(new DateTime(2026, 10, 6, 3, 0, 0)));
}
