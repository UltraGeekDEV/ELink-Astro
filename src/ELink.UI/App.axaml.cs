using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ELink.UI.Infrastructure;
using ELink.UI.ViewModels;
using ELink.UI.Views;
using Event.CoreFunctionality;

namespace ELink.UI;

/// <summary>How the UI is launched. The process that hosts it decides how it reaches the mesh.</summary>
public sealed class UiLaunchOptions
{
    /// <summary>The hosting process's own node (all-in-one station), or null for a UI-only process that joins a mesh.</summary>
    public TypeSafeEVentNode? Node { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5698;
    /// <summary>Join <see cref="Host"/>:<see cref="Port"/> at start (ignored when <see cref="Node"/> is given).</summary>
    public bool AutoConnect { get; init; } = true;
}

public partial class App : Application
{
    public static UiLaunchOptions Options { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            MainViewModel? vm = null;
            desktop.Exit += (_, _) => vm?.Dispose();
            _ = InitAsync(window, v => vm = v);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitAsync(MainWindow window, Action<MainViewModel> created)
    {
        MeshSession? session = null;
        try
        {
            session = await MeshSession.CreateAsync(Options.Node);
            var vm = new MainViewModel(session, showConnectionBar: Options.Node is null) { Host = Options.Host, Port = Options.Port };
            created(vm);
            window.DataContext = vm;
            if (Options.Node is null && Options.AutoConnect) await session.ConnectAsync(Options.Host, Options.Port);
            await vm.StartAsync();
        }
        catch (Exception ex) { if (session is not null) session.Status = "start failed: " + ex.Message; else Console.Error.WriteLine("ELink UI failed to start: " + ex.Message); }
    }
}
