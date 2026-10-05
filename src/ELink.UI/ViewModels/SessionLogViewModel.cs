using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Automation;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

/// <summary>Tonight's log: what the station says as it says it, a box for your own remarks (they go in the same file, stamped
/// with the time), and the whole thing copied in one go for a bug report.</summary>
public sealed partial class SessionLogViewModel : ObservableObject, IDisposable
{
    private const int Keep = 500;
    private readonly MeshSession _mesh;
    private readonly Action<LogEntry> _hook;
    private bool _hooked;

    public SessionLogViewModel(MeshSession mesh) { _mesh = mesh; _hook = e => UiThread.Post(() => Add(Line(e))); }

    public ObservableCollection<string> Lines { get; } = new();
    [ObservableProperty] private string _file = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddNoteCommand))] private string _note = "";
    /// <summary>Set by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }
    [ObservableProperty] private string _copied = "";

    public async Task StartAsync()
    {
        if (_hooked) return;
        _hooked = true;
        await _mesh.Node.HookEventAsync(LogIds.Entry, _hook, "the session log view");
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var answers = await _mesh.Node.CallFunctionAsync<BinaryConvertibleInt32, LogTail>(LogIds.Tail, (BinaryConvertibleInt32)Keep, TimeSpan.FromSeconds(5));
            var tail = answers?.FirstOrDefault();
            if (tail is null) return;
            UiThread.Post(() =>
            {
                File = tail.File.Text;
                Lines.Clear();
                foreach (var l in tail.Text.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Lines.Add(l);
            });
        }
        catch (Exception) { /* no log service: the page stays empty */ }
    }

    private static string Line(LogEntry e) => LogFormat.Line(e);

    private void Add(string line)
    {
        Lines.Add(line);
        while (Lines.Count > Keep) Lines.RemoveAt(0);
    }

    private bool CanAddNote() => Note.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddNote))]
    private void AddNote()
    {
        SessionLog.Note(_mesh.Node, Note.Trim());
        Note = "";
    }

    /// <summary>Everything a bug report needs: the log lines and what ELink is running on.</summary>
    public string ReportText() =>
        $"ELink session log {File}\n{Environment.OSVersion}, .NET {Environment.Version}\n\n{string.Join("\n", Lines)}\n";

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = ReportText();
        if (CopyToClipboard is not null) { try { await CopyToClipboard(text); Copied = $"copied {Lines.Count} lines"; } catch (Exception ex) { Copied = ex.Message; } }
    }

    public void Dispose() { if (_hooked) { try { _mesh.Node.UnhookEvent(LogIds.Entry, _hook); } catch (Exception) { } } }
}
