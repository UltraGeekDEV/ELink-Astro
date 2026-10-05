using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ELink.UI.Controls;
using ELink.UI.ViewModels;

namespace ELink.UI.Views;

public partial class PictureView : UserControl
{
    private PictureViewModel? _vm;
    private Action<IReadOnlyList<Avalonia.Point>>? _flash;

    public PictureView()
    {
        AvaloniaXamlLoader.Load(this);
        var compare = this.FindControl<Button>("CompareButton");
        if (compare is not null)
        {
            compare.AddHandler(PointerPressedEvent, (_, _) => { if (_vm is not null) _vm.ShowOriginal = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            compare.AddHandler(PointerReleasedEvent, (_, _) => { if (_vm is not null) _vm.ShowOriginal = false; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            compare.PointerCaptureLost += (_, _) => { if (_vm is not null) _vm.ShowOriginal = false; };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null && _flash is not null) _vm.Flashed -= _flash;
        _vm = DataContext as PictureViewModel;
        if (_vm is null) return;
        var viewer = this.FindControl<PictureViewer>("Viewer");
        _flash = quad => viewer?.Flash(quad);
        _vm.Flashed += _flash;
    }
}
