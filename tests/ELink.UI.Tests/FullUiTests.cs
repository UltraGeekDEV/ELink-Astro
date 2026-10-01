using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ELink.Atlas;
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

    private static int FreePort() => ELink.Testing.TestPorts.Next();

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
        var skyCatalog = File.Exists("/usr/share/kstars/namedstars.dat") ? await Task.Run(() => AtlasCatalog.LoadKStars())
            : AtlasCatalog.From(new List<CatalogStar> { new(5.92, 7.41, 0.45f, 1.85f, 39801, "Betelgeuse") }, new List<CatalogDso> { new("M 42", "Nebula", "Orion Nebula, NGC 1976", 5.588, -5.39, 4f, 90, 60, 0) });
        await using var atlasSvc = new AtlasService(hostNode, skyCatalog); await atlasSvc.StartAsync();
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

        // mosaic through the UI: paint a small area to 2 s per spot with single shots, watch the coverage map fill
        var mosaic = vm.Mosaic;
        Assert.True(await Eventually(() => mosaic.Scopes.Contains("main")));
        mosaic.SelectedScope = "main";
        mosaic.Label = "UIField"; mosaic.CenterRa = "06:00:00"; mosaic.CenterDec = "10:00:00";
        mosaic.FovWidth = 0.4; mosaic.FovHeight = 0.3; mosaic.Stepover = 0.05;
        mosaic.Frames[0].Width = 0.2; mosaic.Frames[0].Height = 0.2;
        mosaic.AddFrameCommand.Execute(null);                                   // a second, differently shaped frame: heterogeneous
        Assert.Equal(2, mosaic.Frames.Count);
        mosaic.Frames[1].Width = 0.1; mosaic.Frames[1].Height = 0.06; mosaic.Frames[1].Rotation = 30; mosaic.Frames[1].OffsetEast = 0.04;
        mosaic.RemoveFrameCommand.Execute(mosaic.Frames[1]);
        Assert.Single(mosaic.Frames);
        mosaic.ExposureSeconds = 1; mosaic.TargetMinutes = 2.0 / 60; mosaic.MaxVisits = 0;
        await mosaic.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("", mosaic.Message);
        Assert.Contains("hop", mosaic.Summary);
        await mosaic.StartRunCommand.ExecuteAsync(null);
        Assert.Equal("", mosaic.Message);
        Assert.True(await Eventually(() => mosaic.Phase == "Done", 300000), $"{mosaic.Phase} {mosaic.Message}");
        Assert.True(await Eventually(() => mosaic.CoverageText.Contains("100% complete")), mosaic.CoverageText);
        Assert.NotNull(mosaic.Map);
        Assert.True(await Eventually(() => mosaic.Outlines.Count == 1));
        Assert.Contains("shots", mosaic.Progress);
        vm.SelectedTab = vm.Tabs.First(t => t.Content is MosaicViewModel);
        Shot(window, "07d-mosaic");

        // sky atlas: search, select, send the mount there from the chart, see its reticle; hand the target to the mosaic
        var atlas = vm.Atlas;
        vm.SelectedTab = vm.Tabs.First(t => t.Content is AtlasViewModel);
        await atlas.RefreshAsync();
        Assert.True(await Eventually(() => atlas.Stars.Count > 0), atlas.Status);
        atlas.SearchText = "M42";
        await atlas.SearchCommand.ExecuteAsync(null);
        Assert.Equal("M 42", atlas.Results.First().Label);
        Assert.True(await Eventually(() => atlas.Selection?.Label == "M 42"));
        Assert.Equal(5.588, atlas.CenterRa, 2);
        Assert.True(await Eventually(() => atlas.Dsos.Any(d => d.Id == "M 42")), "the zoomed view fetched its deep-sky objects");
        Assert.True(await Eventually(() => atlas.Pointers.Contains("eq")));
        atlas.SelectedPointer = "eq";
        await atlas.GotoCommand.ExecuteAsync(null);
        Assert.Contains("going to M 42", atlas.Message);
        Assert.True(await Eventually(() => atlas.Markers.Any(m => !m.IsSelection && Math.Abs(m.RaHours - 5.588) < 0.05 && Math.Abs(m.DecDegrees + 5.39) < 0.3), 120000),
            "the mount's reticle should arrive on M 42");
        atlas.UseAsMosaicCentreCommand.Execute(null);
        Assert.Equal("M42", vm.Mosaic.Label);
        Assert.Contains(atlas.Polygons, p => p.Label.StartsWith("mosaic"));
        // a click right on Betelgeuse picks it
        atlas.CenterRa = 5.92; atlas.CenterDec = 7.4; atlas.Fov = 20;
        await atlas.RefreshAsync();
        Assert.True(await Eventually(() => atlas.Stars.Any(s => s.Label == "Betelgeuse")));
        atlas.PickCommand.Execute((5.9195, 7.4071, 40.0));
        Assert.Equal("Betelgeuse", atlas.Selection!.Label);
        Shot(window, "07e-atlas");

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
