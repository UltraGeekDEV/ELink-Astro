using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ELink.UI.Infrastructure;

/// <summary>Colours for status chips and dots by their style word (idle | busy | ok | warn | error): bindable where a
/// style class cannot be switched from a view model.</summary>
public static class Chips
{
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static readonly IValueConverter Background = new FuncValueConverter<string?, IBrush?>(c => c switch
    {
        "ok" => Brush("#1B3A2E"), "busy" => Brush("#1F3558"), "warn" => Brush("#43361A"), "error" => Brush("#4A2226"), _ => Brush("#272F40"),
    });

    public static readonly IValueConverter Dot = new FuncValueConverter<string?, IBrush?>(c => c switch
    {
        "ok" => Brush("#4CC980"), "busy" => Brush("#5C9DFF"), "warn" => Brush("#F2B94B"), "error" => Brush("#FF7B72"), _ => Brush("#5A667C"),
    });

    public static readonly IValueConverter ConnectText = new FuncValueConverter<bool, string>(connected => connected ? "Disconnect" : "Connect");
    public static readonly IValueConverter IsError = new FuncValueConverter<string?, bool>(c => c == "error");
    /// <summary>"" shown as "none" (for lists where the empty entry means no device).</summary>
    public static readonly IValueConverter NoneText = new FuncValueConverter<string?, string>(t => string.IsNullOrEmpty(t) ? "none" : t);
    /// <summary>The Sky's side panel: about a third of the window, between 330 and 410.</summary>
    public static readonly IValueConverter PanelWidth = new FuncValueConverter<double, double>(w => Math.Clamp(w * 0.36, 330, 410));
    /// <summary>true/false as Yes/No.</summary>
    public static readonly IValueConverter YesNo = new FuncValueConverter<bool, string>(b => b ? "Yes" : "No");

    /// <summary>"#RRGGBB" to a brush (the scope colours).</summary>
    public static readonly IValueConverter Hex = new FuncValueConverter<string?, IBrush?>(h => h is { Length: > 0 } ? new SolidColorBrush(Color.Parse(h)) : null);
}
