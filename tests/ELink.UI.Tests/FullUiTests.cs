using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.IndiBridge;
using ELink.UI.Infrastructure;
using ELink.UI.ViewModels;
using ELink.UI.Views;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The UI against the real simulated stack, rendered headless. The UI process only joins a mesh: it has no
/// reference to the bridge or the composition host, which run as separate EVent nodes.</summary>
public class FullUiTests : IClassFixture<IndiServerFixture>
{
    private readonly IndiServerFixture _server;
    public FullUiTests(IndiServerFixture server) { _server = server; }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private static string ShotDir()
    {
        var dir = Environment.GetEnvironmentVariable("ELINK_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "elink-screens");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Shot(Avalonia.Controls.Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir(), name + ".png"));
    }

    [AvaloniaFact]
    public async Task UiDrivesTheSimulatedStack()
    {
        Assert.True(_server.Available);
        // backend: INDI bridge and composition host, each its own node
        int bridgePort = FreePort();
        using var bridge = await Task.Run(() => ElinkNode.Create("UI-Bridge", bridgePort));
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        await using var link = new IndiServerLink(bridge, dir, "sim", "127.0.0.1", _server.Port);
        await link.StartAsync();
        using var hostNode = await Task.Run(() => ElinkNode.Create("UI-Host", FreePort()));
        await ElinkNode.JoinAsync(hostNode, "127.0.0.1", bridgePort);
        await using var host = new CompositionHost(hostNode);
        await host.StartAsync();
        await using var autofocus = new AutofocusService(hostNode); await autofocus.StartAsync();
        await using var sequencer = new SequencerService(hostNode); await sequencer.StartAsync();
        await using var mosaicSvc = new MosaicService(hostNode); await mosaicSvc.StartAsync();
        string saveDir = Path.Combine(Path.GetTempPath(), "elink-ui-save-" + Guid.NewGuid().ToString("N"));
        await using var storage = new StorageService(hostNode, saveDir); await storage.StartAsync();

        // frontend: a separate node that joins the mesh
        var session = await MeshSession.CreateAsync(null, "UI-Test");
        var vm = new MainViewModel(session);
        vm.Port = bridgePort;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.StartAsync();
        Assert.True(await Eventually(() => vm.Catalog.OfKind("Mount").Any() && vm.Catalog.OfKind("Camera").Any() && vm.Catalog.OfKind("FilterWheel").Any()), "catalog empty");
        Shot(window, "01-catalog-composer");

        // connect and open the mount
        foreach (var d in vm.Catalog.Devices.Where(d => d.Kind is "Mount" or "Camera" or "FilterWheel").ToList())
            await Commands.CallAsync(session.Node, EquipmentIds.Command(d.Kind, d.Id, "Connect"), (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleBool)true);
        await Commands.CallAsync(session.Node, EquipmentIds.Command("Mount", "Telescope_Simulator", "Park"), (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleBool)false);
        var mountItem = vm.Catalog.Devices.First(d => d.Kind == "Mount");
        await vm.OpenDeviceCommand.ExecuteAsync(mountItem);
        var mount = Assert.IsType<MountPanelViewModel>(vm.SelectedTab!.Content);
        Assert.True(await Eventually(() => mount.Connected && mount.Phase != "Parked"));
        mount.TargetRa = "10:00:00"; mount.TargetDec = "20:00:00";
        await mount.GotoCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => mount.Phase == "Slewing"));
        await Task.Delay(400);
        Shot(window, "02-mount-slewing");
        Assert.True(await Eventually(() => mount.Phase == "Tracking", 90000));
        Shot(window, "03-mount-tracking");

        // camera: take a frame, see it
        var camItem = vm.Catalog.Devices.First(d => d.Kind == "Camera");
        await vm.OpenDeviceCommand.ExecuteAsync(camItem);
        var cam = Assert.IsType<CameraPanelViewModel>(vm.SelectedTab!.Content);
        Assert.True(await Eventually(() => cam.Connected));
        cam.ExposureSeconds = 1;
        await cam.ExposeCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => cam.Preview.Image is not null, 30000), "no preview: " + cam.Preview.Info + " " + cam.Message);
        Shot(window, "04-camera-frame");

        // compose a scope in the UI, observe it
        var composer = vm.Composer;
        composer.NewPointerId = "eq"; composer.SelectedMount = "Telescope_Simulator";
        await composer.DefinePointerCommand.ExecuteAsync(null);
        composer.NewShooterId = "cam"; composer.SelectedCamera = "CCD_Simulator"; composer.SelectedWheel = "Filter_Simulator";
        await composer.DefineShooterCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => composer.PointerChoices.Any(c => c.Id == "eq") && composer.ShooterChoices.Any(c => c.Id == "cam")));
        composer.ScopeId = "main"; composer.ScopeName = "Main scope";
        composer.PointerChoices.First(c => c.Id == "eq").IsSelected = true;
        composer.ShooterChoices.First(c => c.Id == "cam").IsSelected = true;
        await composer.DefineScopeCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => vm.Catalog.Scopes.Any(s => s.Id == "main")), composer.Message);
        vm.SelectedTab = vm.Tabs.First(t => t.Content is ComposerViewModel);
        Shot(window, "05-composer");

        await vm.OpenScopeCommand.ExecuteAsync(vm.Catalog.Scopes.First(s => s.Id == "main"));
        var scope = Assert.IsType<ScopePanelViewModel>(vm.SelectedTab!.Content);
        scope.TargetRa = "05:00:00"; scope.TargetDec = "30:00:00"; scope.ExposureSeconds = 1; scope.Filter = "Green"; scope.Count = 2;
        await scope.ObserveCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => scope.Observing));
        Assert.True(await Eventually(() => scope.ScopePhase is "Exposing", 90000));
        Shot(window, "06-scope-observing");
        Assert.True(await Eventually(() => !scope.Observing && scope.ShotsDone == 2, 120000), $"{scope.ScopePhase} {scope.ScopeMessage}");
        Assert.True(await Eventually(() => scope.Preview.Image is not null));
        Assert.True(await Eventually(() => scope.Shots.Count == 2), $"shots: {scope.Shots.Count}");
        Shot(window, "07-scope-done");

        // sequencer through the UI: one block, one frame
        var store = vm.Storage;
        Assert.True(await Eventually(() => store.Shooters.Contains("main")));
        store.SelectedShooter = "main";
        await store.WatchCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => store.Watching.Contains("main")));
        var seq = vm.Sequencer;
        Assert.True(await Eventually(() => seq.Scopes.Contains("main")));
        seq.SelectedScope = "main";
        seq.Blocks[0].Ra = "04:00:00"; seq.Blocks[0].Dec = "25:00:00"; seq.Blocks[0].Seconds = 1; seq.Blocks[0].Count = 1; seq.Blocks[0].Label = "UI block";
        await seq.StartPlanCommand.ExecuteAsync(null);
        Assert.Equal("", seq.Message);
        Assert.True(await Eventually(() => seq.Phase == "Done", 120000), $"{seq.Phase} {seq.StateMessage}");
        Assert.Contains("1 total", seq.Progress);
        Assert.True(await Eventually(() => store.FramesSaved >= 1), "the frame was not saved");
        Assert.True(await Eventually(() => store.Recent.Count >= 1));
        Assert.Single(Directory.GetFiles(saveDir, "UI_block_*.fits", SearchOption.AllDirectories));
        vm.SelectedTab = vm.Tabs.First(t => t.Content is SequencerViewModel);
        Shot(window, "07b-sequencer");
        vm.SelectedTab = vm.Tabs.First(t => t.Content is AutofocusViewModel);
        Assert.True(await Eventually(() => vm.Autofocus.Shooters.Contains("cam") && vm.Autofocus.Focusers.Contains("Focuser_Simulator")));
        Shot(window, "07c-autofocus");

        // mosaic through the UI: preview the tiling, scan 3 panels twice with single shots, watch the coverage map fill
        var mosaic = vm.Mosaic;
        Assert.True(await Eventually(() => mosaic.Scopes.Contains("main")));
        mosaic.SelectedScope = "main";
        mosaic.Label = "UIField"; mosaic.CenterRa = "06:00:00"; mosaic.CenterDec = "10:00:00";
        mosaic.FovWidth = 1.0; mosaic.FovHeight = 0.5; mosaic.FrameWidth = 0.5; mosaic.FrameHeight = 0.5; mosaic.Overlap = 0.2;
        mosaic.ExposureSeconds = 1; mosaic.Passes = 2;
        await mosaic.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("", mosaic.Message);
        Assert.Contains("3 × 1 = 3 panels", mosaic.Summary);
        Assert.Equal(3, mosaic.Cells.Count);
        await mosaic.StartRunCommand.ExecuteAsync(null);
        Assert.Equal("", mosaic.Message);
        Assert.True(await Eventually(() => mosaic.Phase == "Done", 240000), $"{mosaic.Phase} {mosaic.Message}");
        Assert.True(await Eventually(() => mosaic.Cells.All(c => c.Frames == 2)), string.Join(",", mosaic.Cells.Select(c => c.Frames)));
        Assert.Contains("6 shots", mosaic.Progress);
        vm.SelectedTab = vm.Tabs.First(t => t.Content is MosaicViewModel);
        Shot(window, "07d-mosaic");

        // generic INDI browser
        vm.SelectedTab = vm.Tabs.First(t => t.Content is IndiBrowserViewModel);
        await vm.IndiBrowser.RefreshServersCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => vm.IndiBrowser.SelectedServer == "sim"));
        Assert.True(await Eventually(() => vm.IndiBrowser.Devices.Count >= 5));
        vm.IndiBrowser.SelectedDevice = "Telescope Simulator";
        Assert.True(await Eventually(() => vm.IndiBrowser.Properties.Count > 10));
        Shot(window, "08-indi-browser");

        vm.Dispose();
        window.Close();
        try { Directory.Delete(saveDir, true); } catch { }
    }
}
