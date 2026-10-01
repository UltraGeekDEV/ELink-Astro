using Avalonia;
using Avalonia.Fonts.Inter;
using ELink.UI;

// ELink UI as its own process: joins an EVent mesh (a bridge, a composition host, ...) and shows what it finds.
//   elink-ui [--host 127.0.0.1] [--port 5698]
namespace ELink.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string host = "127.0.0.1"; int port = 5698;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else { Console.WriteLine("usage: elink-ui [--host 127.0.0.1] [--port 5698]"); return args[i] is "-h" or "--help" ? 0 : 1; }
        }
        UI.App.Options = new UiLaunchOptions { Host = host, Port = port };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UI.App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
