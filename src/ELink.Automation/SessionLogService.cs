using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.Connections.Models.BaseBinaryConvertibles;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using EVent.Endpoints;

namespace ELink.Automation;

/// <summary>The session log: a plain-text file per night (the night that starts in the evening keeps its date until noon), written
/// from <see cref="LogEntry"/> events. It also follows what the scopes, autofocus, centring and the stacks report and writes the
/// changes down, so the file reads as the story of the night; the observer's notes go in the same file. The folder is served by an
/// EVent file endpoint, so any node (the web page too) can read the files, and append to them, but not replace them.</summary>
public sealed class SessionLogService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _folder;
    private readonly FileEndpoint _files;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteState<ScopeState>> _scopes = new();
    private readonly Dictionary<string, string> _last = new();
    private readonly List<IDisposable> _follows = new();
    private readonly Action<LogEntry> _entryHook;
    private readonly Func<BinaryConvertibleInt32, LogTail> _tail;
    private readonly Action<CompositionSnapshot> _compositionHook;
    private readonly Action<StackFrameAdded> _frameHook;

    public SessionLogService(TypeSafeEVentNode node, string folder)
    {
        _node = node; _folder = folder;
        Directory.CreateDirectory(folder);
        _files = new FileEndpoint(node, folder, allowSave: true, name: LogIds.FileRoot) { Authorize = (_, action, _) => action != FileAction.Save };
        _entryHook = e => Append(e);
        _tail = n => TailOf(n.Value);
        _compositionHook = FollowScopes;
        _frameHook = f => Write("stack", "info", $"{f.Stack.Text}: {f.Frames.Value} frames{(f.Source.Text != "" ? " (latest from " + f.Source.Text + ")" : "")}");
    }

    /// <summary>The night a moment belongs to: after noon it is that day's evening, before noon the day before.</summary>
    public static string FileNameFor(DateTime local) => $"session-{local.AddHours(-12):yyyy-MM-dd}.log";
    public string CurrentFile => Path.Combine(_folder, FileNameFor(DateTime.Now));

    public async Task StartAsync()
    {
        await _node.HookEventAsync(LogIds.Entry, _entryHook, "a line for the session log");
        await _node.RegisterFunctionAsync(LogIds.Tail, _tail, "the end of tonight's log and the file it is in");
        await _node.HookEventAsync(ScopeIds.Changed, _compositionHook, "session log: follows the scopes");
        await _node.HookEventAsync(LiveStackIds.FrameAdded, _frameHook, "session log: frames landing in a stack");
        Write("station", "info", "session log open");
        await FollowAsync<AutofocusState>("autofocus", AutofocusIds.State, AutofocusIds.GetState, s => (s.Phase.Text, s.Message.Text));
        await FollowAsync<CenteringState>("centring", CenteringIds.State, CenteringIds.GetState, s => (s.Phase.Text, s.Message.Text));
        try
        {
            var snaps = await _node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void, TimeSpan.FromSeconds(3));
            foreach (var s in snaps ?? new()) FollowScopes(s);
        }
        catch (Exception) { /* no composition host yet: the first Changed event brings the scopes */ }
    }

    // ---- what the rest of the station says ---------------------------------------------------------------------

    private async Task FollowAsync<T>(string source, string stateId, string getId, Func<T, (string Phase, string Message)> read) where T : IBinaryConvertible, new()
    {
        var follow = new RemoteState<T>(_node, stateId, getId);
        follow.Changed += s => { var (phase, message) = read(s); Changed(source, phase, message); };
        lock (_gate) _follows.Add(follow);
        await follow.StartAsync();
    }

    private void FollowScopes(CompositionSnapshot snapshot)
    {
        foreach (var scope in snapshot.Scopes)
        {
            string id = scope.Id.Text;
            RemoteState<ScopeState> follow;
            lock (_gate)
            {
                if (_scopes.ContainsKey(id)) continue;
                follow = _scopes[id] = new RemoteState<ScopeState>(_node, ScopeIds.State(id), ScopeIds.GetState(id));
            }
            follow.Changed += s => Changed(id, s.Phase.Text, s.Message.Text, s.Observing.Value && s.ShotsPlanned.Value > 0 ? $" ({s.ShotsDone.Value}/{s.ShotsPlanned.Value})" : "");
            _ = follow.StartAsync();
        }
    }

    // A line when the phase or the message of a follower changes, not for every counter that moves.
    private void Changed(string source, string phase, string message, string suffix = "")
    {
        string key = phase + "|" + message;
        lock (_gate) { if (_last.TryGetValue(source, out var l) && l == key) return; _last[source] = key; }
        string level = phase == "Error" ? "error" : "info";
        Write(source, level, message == "" ? phase : $"{phase}: {message}{suffix}");
    }

    // what the log itself notices goes out as an entry too, so every view of the log sees it (and it is written when it arrives)
    private void Write(string source, string level, string text) => SessionLog.Write(_node, source, level, text);

    // ---- the file --------------------------------------------------------------------------------------------

    private void Append(LogEntry entry)
    {
        if (entry.TimeUtc.Text == "") entry.TimeUtc = DateTime.UtcNow.ToString("o");
        try { lock (_gate) File.AppendAllText(CurrentFile, LogFormat.Line(entry) + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"[log] {ex.Message}"); }
    }

    private LogTail TailOf(int lines)
    {
        string file = CurrentFile;
        string text = "";
        try
        {
            lock (_gate)
            {
                if (File.Exists(file))
                {
                    var all = File.ReadAllLines(file);
                    text = string.Join("\n", all.Skip(Math.Max(0, all.Length - Math.Clamp(lines, 1, 5000))));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new LogTail { File = file, Text = text };
    }

    public ValueTask DisposeAsync()
    {
        Append(new LogEntry { Source = "station", Text = "session log closed" });
        try { _node.UnhookEvent(LogIds.Entry, _entryHook); _node.UnhookEvent(ScopeIds.Changed, _compositionHook); _node.UnhookEvent(LiveStackIds.FrameAdded, _frameHook); } catch (Exception) { }
        try { _node.UnregisterFunction(LogIds.Tail, _tail); } catch (Exception) { }
        lock (_gate) { foreach (var f in _follows) f.Dispose(); foreach (var s in _scopes.Values) s.Dispose(); _follows.Clear(); _scopes.Clear(); }
        _files.Dispose();
        return ValueTask.CompletedTask;
    }
}
