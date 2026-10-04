using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ELink.UI.Controls;

/// <summary>A message line with a coloured edge, shown only when it has text. Kind: ok | error | warn | info (default error:
/// a view model that only ever sets a message when something went wrong needs nothing else).</summary>
public sealed class Notice : Border
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Notice, string?>(nameof(Text));
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Notice, string?>(nameof(Kind), "error");

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };

    public Notice()
    {
        Child = _text;
        Padding = new Thickness(12, 8);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(3, 0, 0, 0);
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == KindProperty) Apply();
    }

    private void Apply()
    {
        _text.Text = Text;
        IsVisible = !string.IsNullOrWhiteSpace(Text);
        (Color edge, Color back, Color fore) = (Kind ?? "error") switch
        {
            "ok" => (Color.FromRgb(0x4C, 0xC9, 0x80), Color.FromRgb(0x18, 0x2E, 0x26), Color.FromRgb(0xA8, 0xEC, 0xC4)),
            "warn" => (Color.FromRgb(0xF2, 0xB9, 0x4B), Color.FromRgb(0x33, 0x2B, 0x18), Color.FromRgb(0xF7, 0xDA, 0x9C)),
            "info" => (Color.FromRgb(0x5C, 0x9D, 0xFF), Color.FromRgb(0x1A, 0x27, 0x3D), Color.FromRgb(0xBF, 0xD8, 0xFF)),
            _ => (Color.FromRgb(0xFF, 0x7B, 0x72), Color.FromRgb(0x38, 0x1E, 0x22), Color.FromRgb(0xFF, 0xC4, 0xBF)),
        };
        BorderBrush = new SolidColorBrush(edge); Background = new SolidColorBrush(back); _text.Foreground = new SolidColorBrush(fore);
    }
}
