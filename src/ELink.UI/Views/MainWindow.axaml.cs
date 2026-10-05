using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ELink.UI.ViewModels;

namespace ELink.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow() { AvaloniaXamlLoader.Load(this); }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // the window is what can ask for a file: the view models only ask the window
        if (DataContext is MainViewModel vm)
        {
            vm.SessionLog.CopyToClipboard = async text => { if (Clipboard is { } c) await c.SetTextAsync(text); };
            vm.Sky.Framing.PickFile = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "A picture of the sky to frame from", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Pictures and FITS") { Patterns = ["*.fits", "*.fit", "*.fts", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff"] }],
                });
                return files.FirstOrDefault()?.TryGetLocalPath();
            };
        }
    }
}
