using Avalonia;
using Avalonia.Headless;
using Avalonia.Fonts.Inter;
using ELink.UI;

[assembly: AvaloniaTestApplication(typeof(ELink.UI.Tests.TestAppBuilder))]

namespace ELink.UI.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().WithInterFont()
            .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
