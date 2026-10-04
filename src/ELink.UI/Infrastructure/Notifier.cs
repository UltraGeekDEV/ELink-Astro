namespace ELink.UI.Infrastructure;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>Something worth telling the person, wherever they are in the app.</summary>
public sealed record Notice(NoticeKind Kind, string Text, string Source, DateTime At)
{
    public string Css => Kind switch { NoticeKind.Success => "ok", NoticeKind.Warning => "warn", NoticeKind.Error => "error", _ => "info" };
}

/// <summary>The app's one place for messages that should not stay buried in the page they came from: view models post here,
/// the shell shows them as toasts and keeps a short history.</summary>
public sealed class Notifier
{
    public event Action<Notice>? Posted;
    public void Post(NoticeKind kind, string text, string source = "") { if (text != "") Posted?.Invoke(new Notice(kind, text, source, DateTime.Now)); }
    public void Error(string text, string source = "") => Post(NoticeKind.Error, text, source);
    public void Success(string text, string source = "") => Post(NoticeKind.Success, text, source);
    public void Info(string text, string source = "") => Post(NoticeKind.Info, text, source);
}
