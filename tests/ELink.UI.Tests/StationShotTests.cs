using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ELink.Atlas;
using ELink.UI.Infrastructure;
using ELink.UI.ViewModels;
using ELink.UI.Views;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The desktop window joined to a real station (ELINK_STATION=host:port, e.g. the Pi), carrying its own sky atlas
/// as elink-ui does, every view rendered: what a person sees, for a usability review (ELINK_SCREENSHOTS=dir). Does nothing
/// without ELINK_STATION.</summary>
public class StationShotTests
{
    [AvaloniaFact]
    public async Task EveryViewAgainstARealStation()
    {
        if (Environment.GetEnvironmentVariable("ELINK_STATION") is not { } station) return;
        var (host, port) = (station.Split(':')[0], int.Parse(station.Split(':')[1]));
        var session = await MeshSession.CreateAsync(null, "UI-Review");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var pack = Path.Combine(dir.FullName, "src", "ELink.App", "sky", SkyPack.FileName);
            if (!File.Exists(pack)) continue;
            var catalog = await Task.Run(() => SkyPack.Read(pack));
            await new AtlasService(session.Node, catalog).StartAsync();
            await new TonightService(session.Node, catalog).StartAsync();
            break;
        }
        var vm = new MainViewModel(session) { Host = host, Port = port };
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = int.TryParse(Environment.GetEnvironmentVariable("ELINK_SHOT_HEIGHT"), out var h) ? h : 900 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.StartAsync();
        await UiRig.Eventually(() => vm.Catalog.Scopes.Count > 0, 20000);
        await Task.Delay(4000);
        void Shot(string name)
        {
            for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
            window.CaptureRenderedFrame()?.Save(Path.Combine(UiRig.ShotDir(), "station-" + name + ".png"));
        }
        foreach (var (view, section, name) in new (AppView, string?, string)[]
        {
            (AppView.Sky, null, "sky"), (AppView.Scopes, null, "scopes"), (AppView.Picture, null, "picture"), (AppView.Rig, "Set up", "rig-setup"),
            (AppView.Rig, "Equipment", "rig-equipment"), (AppView.Rig, "Site", "rig-site"), (AppView.Rig, "Calibration", "rig-calibration"),
            (AppView.Advanced, "Help", "adv-help"), (AppView.Advanced, "Saving frames", "adv-saving"),
        })
        {
            vm.Navigate(view, section);
            await Task.Delay(1500);
            Shot(name);
        }
        // planning the Pleiades from the chart, as a person would
        vm.Navigate(AppView.Sky);
        vm.Sky.Atlas.SearchText = "M45";
        await vm.Sky.Atlas.SearchCommand.ExecuteAsync(null);
        await Task.Delay(2500);
        Shot("sky-m45");
        window.Close(); vm.Dispose();
    }
}
