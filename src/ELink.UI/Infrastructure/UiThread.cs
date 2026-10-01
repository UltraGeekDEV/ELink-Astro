using Avalonia.Threading;

namespace ELink.UI.Infrastructure;

/// <summary>Where mesh callbacks (which arrive on thread-pool threads) hop onto the UI thread. Replaceable so view
/// models can be tested without a UI.</summary>
public static class UiThread
{
    public static Action<Action> Post { get; set; } = a => Dispatcher.UIThread.Post(a);
}
