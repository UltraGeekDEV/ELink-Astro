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
using EVent.Connections.Models.BaseBinaryConvertibles;
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
        await using var scheduler = new SchedulerService(hostNode); await scheduler.StartAsync();
        await using var profilesSvc = new ProfileService(hostNode, new DeviceDirectory(hostNode)); await profilesSvc.StartAsync();
        await using var imagingSvc = new ImagingService(hostNode); await imagingSvc.StartAsync();
        var skyCatalog = File.Exists("/usr/share/kstars/namedstars.dat") ? await Task.Run(() => AtlasCatalog.LoadKStars())
            : AtlasCatalog.From(new List<CatalogStar> { new(5.92, 7.41, 0.45f, 1.85f, 39801, "Betelgeuse") }, new List<CatalogDso> { new("M 42", "Nebula", "Orion Nebula, NGC 1976", 5.588, -5.39, 4f, 90, 60, 0) });
        await using var atlasSvc = new AtlasService(hostNode, skyCatalog); await atlasSvc.StartAsync();
        await using var centeringSvc = new CenteringService(hostNode); await centeringSvc.StartAsync();
        await using var liveStackSvc = new LiveStackService(hostNode); await liveStackSvc.StartAsync();
        await using var siteSvc = new SiteService(hostNode); await siteSvc.StartAsync();
        PlateSolveService? solveSvc = PlateSolver.Locate() is { } sf ? new PlateSolveService(hostNode, new PlateSolver(sf)) : null;
        if (solveSvc is not null) await solveSvc.StartAsync();
        string saveDir = Path.Combine(Path.GetTempPath(), "elink-ui-save-" + Guid.NewGuid().ToString("N"));
        await using var storage = new StorageService(hostNode, saveDir); await storage.StartAsync();
        await using var calibrationSvc = new CalibrationService(hostNode, saveDir); await calibrationSvc.StartAsync();

        // frontend: a separate node that joins the mesh
        var session = await MeshSession.CreateAsync(null, "UI-Test");
        var vm = new MainViewModel(session);
        vm.Port = bridgePort;
        // ELINK_SHOT_HEIGHT=2600 renders every page at full length (for design review); the default keeps the usual laptop-sized window
        int shotHeight = int.TryParse(Environment.GetEnvironmentVariable("ELINK_SHOT_HEIGHT"), out var sh) && sh >= 600 ? sh : 820;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = shotHeight };
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
        await vm.OpenDeviceAsync(mountItem);
        var mount = Assert.IsType<MountPanelViewModel>(vm.Drawer);
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
        await vm.OpenDeviceAsync(camItem);
        var cam = Assert.IsType<CameraPanelViewModel>(vm.Drawer);
        Assert.True(await Eventually(() => cam.Connected));
        cam.ExposureSeconds = 1;
        await cam.ExposeCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => cam.Preview.Image is not null, 30000), "no preview: " + cam.Preview.Info + " " + cam.Message);
        Shot(window, "04-camera-frame");

        // compose a scope in the UI, observe it
        var composer = vm.Composer;
        vm.Navigate(AppView.Rig, "Set up");
        // an explicit pointer (scopes make their own; this is the advanced route)
        composer.NewPointerId = "eq"; composer.SelectedMount = "Telescope_Simulator";
        await composer.DefinePointerCommand.ExecuteAsync(null);
        Assert.Equal("ok", composer.PointerMessageKind);
        // a telescope: optics, the CCD behind the filter wheel
        Assert.True(await Eventually(() => composer.TrainCameras.Any(c => c.CameraId == "CCD_Simulator")));
        composer.NewTrainCommand.Execute(null);
        composer.TrainName = "cam"; composer.FocalLength = 400; composer.SelectedWheel = "Filter_Simulator";
        foreach (var c in composer.TrainCameras) c.Role = c.CameraId == "CCD_Simulator" ? "Imaging" : ComposerViewModel.NotInTrain;
        composer.TrainCameras.First(c => c.CameraId == "CCD_Simulator").Gain = "30";
        composer.FocusOffsets = "Red=0, Green=nonsense";
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.Contains("focus offsets are like", composer.TrainMessage);
        composer.FocusOffsets = "Red=0, Green=25";
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => vm.Catalog.Composition.Trains.FirstOrDefault(t => t.Id.Text == "cam") is { } t && t.FocusOffsets.Count == 2 && t.Cameras[0].Gain.Value == 30), composer.TrainMessage);
        Assert.False(composer.TrainFormOpen);
        // it can be edited: the form loads what is there
        Assert.True(await Eventually(() => composer.Trains.Any(t => t.Id == "cam")));
        composer.EditTrainCommand.Execute(composer.Trains.First(t => t.Id == "cam"));
        Assert.Equal("30", composer.TrainCameras.First(c => c.CameraId == "CCD_Simulator").Gain);
        Assert.Equal("Red=0, Green=25", composer.FocusOffsets);
        composer.CancelTrainCommand.Execute(null);
        // a scope on that mount with that telescope
        Assert.True(await Eventually(() => composer.PointerChoices.Any(c => c.Id == "eq") && composer.ShooterChoices.Any(c => c.Id == "cam")));
        composer.NewScopeCommand.Execute(null);
        composer.ScopeName = "main";
        composer.PointerChoices.First(c => c.Id == "eq").IsSelected = true;
        composer.ShooterChoices.First(c => c.Id == "cam").IsSelected = true;
        await composer.SaveScopeCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => vm.Catalog.Scopes.Any(s => s.Id == "main")), composer.ScopeMessage);
        vm.Navigate(AppView.Rig, "Set up");
        Shot(window, "05-composer");

        Assert.True(await Eventually(() => vm.Scopes.Cards.Any(c => c.ScopeId == "main")));
        vm.Scopes.Select("main"); vm.Navigate(AppView.Scopes);
        var scope = Assert.IsType<ScopePanelViewModel>(vm.Scopes.Selected);
        scope.TargetRa = "05:00:00"; scope.TargetDec = "30:00:00"; scope.ExposureSeconds = 1; scope.Filter = "Green"; scope.Count = 2;
        await scope.ObserveCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => scope.Observing));
        Assert.True(await Eventually(() => scope.ScopePhase is "Exposing", 90000));
        Shot(window, "06-scope-observing");
        Assert.True(await Eventually(() => !scope.Observing && scope.ShotsDone == 2, 120000), $"{scope.ScopePhase} {scope.ScopeMessage}");
        Assert.True(await Eventually(() => scope.Preview.Image is not null));
        Assert.True(await Eventually(() => scope.Shots.Count == 2), $"shots: {scope.Shots.Count}");
        Shot(window, "07-scope-done");

        // storage: the main scope's frames are saved (the Image run below makes some)
        var store = vm.Storage;
        Assert.True(await Eventually(() => store.Shooters.Contains("main")));
        store.SelectedShooter = "main";
        await store.WatchCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => store.Watching.Contains("main")));
        vm.Navigate(AppView.Scopes);
        Assert.True(await Eventually(() => vm.Autofocus.Shooters.Contains("cam") && vm.Autofocus.Focusers.Contains("Focuser_Simulator")));
        Shot(window, "07c-autofocus");

        // an image through the UI: an area bigger than a frame of the "cam" train, filled to one exposure per spot, built live
        var image = vm.Image;
        Assert.True(await Eventually(() => image.Scopes.Any(c => c.Id == "main")));
        foreach (var c in image.Scopes) c.Selected = c.Id == "main";
        image.Label = "UIField"; image.CenterRa = "06:00:00"; image.CenterDec = "10:00:00";
        image.Width = 1.3; image.Height = 1.0; image.ExposureSeconds = 1; image.TargetMinutes = 1.0 / 60; image.MaxVisits = 0;
        image.LiveStack = true; image.OutputScale = 20; image.DitherArcsec = 20;
        await image.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("", image.Message);
        Assert.Contains("main:", image.Summary);
        await image.StartRunCommand.ExecuteAsync(null);
        // accepted (it may say it is learning the camera's angle first: an information, not an error)
        Assert.True(image.Message == "" || image.Message.StartsWith("learning"), image.Message);
        Assert.True(await Eventually(() => image.Phase is "Done" or "Error", 400000), $"{image.Phase} {image.Message}");
        Assert.Equal("Done", image.Phase);
        Assert.True(await Eventually(() => image.CoverageText.Contains("100% complete")), image.CoverageText);
        Assert.NotNull(image.Map);
        Assert.Contains(image.Workers, w => w.StartsWith("main: Done"));
        Assert.True(await Eventually(() => image.Stack.Image is not null, 120000), image.Stack.Info);
        // the picture and how far each part has got are drawn on the sky chart, where the image is
        vm.Navigate(AppView.Sky);
        Assert.True(await Eventually(() => vm.Sky.Images.Count == 2), $"layers on the chart: {vm.Sky.Images.Count}");
        vm.Sky.ZoomToFrameCommand.Execute(null);
        await Task.Delay(500);
        Shot(window, "sky-image-built");
        Assert.True(await Eventually(() => store.FramesSaved >= 1), "the image's frames were saved");
        Assert.NotEmpty(Directory.GetFiles(saveDir, "UIField_*.fits", SearchOption.AllDirectories));
        // and onto the schedule (it waits for a site: none is set yet)
        await image.AddToScheduleCommand.ExecuteAsync(null);
        Assert.Contains("queue", image.Message);
        Assert.True(await Eventually(() => vm.Schedule.Rows.Any(r => r.Label == "UIField")));
        Assert.True(await Eventually(() => vm.Schedule.Rows.First(r => r.Label == "UIField").Status.Contains("site")), vm.Schedule.Rows.First().Status);
        vm.Navigate(AppView.Sky);
        Shot(window, "07d2-schedule");
        vm.Navigate(AppView.Sky);
        Shot(window, "07d-image");

        // site: type a location; tonight's sky appears, and the atlas gets a horizon, the planets and visibility
        var siteVm = vm.Site;
        vm.Navigate(AppView.Rig, "Site");
        siteVm.Latitude = "47:29:52"; siteVm.Longitude = "19:02:25"; siteVm.Elevation = 100; siteVm.MinAltitude = 15;
        siteVm.HorizonText = "180:15, 200:35";
        Assert.True(SiteViewModel.TryParseHorizon(siteVm.HorizonText, out var hp) && hp.Count == 2);
        siteVm.HorizonText = "180:15, 200:35, 260:35, 280:15";
        await siteVm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("saved", siteVm.Message);
        Assert.True(await Eventually(() => siteVm.Known && siteVm.SkyText.Contains("sidereal")), siteVm.StateMessage);
        Assert.Contains("Moon", siteVm.MoonText);
        Assert.True(await Eventually(() => siteVm.Bodies.Count == 9));
        Assert.Equal(47.4978, siteSvc.Site!.Value.LatitudeDegrees, 3);
        Shot(window, "07e0-site");

        // sky atlas: search, select, send the mount there from the chart, see its reticle; hand the target to the mosaic
        var atlas = vm.Atlas;
        vm.Navigate(AppView.Sky);
        await atlas.RefreshAsync();
        Assert.True(await Eventually(() => atlas.Stars.Count > 0), atlas.Status);
        atlas.SearchText = "M42";
        await atlas.SearchCommand.ExecuteAsync(null);
        Assert.Equal("M 42", atlas.Results.First().Label);
        Assert.True(await Eventually(() => atlas.Selection?.Label == "M 42"));
        Assert.True(await Eventually(() => atlas.Horizon is { Line.Count: 361 } && atlas.Bodies.Count == 9), "horizon and planets on the chart");
        Assert.True(await Eventually(() => atlas.VisibilityText.Contains("alt")), atlas.VisibilityText);
        Assert.Equal(5.588, atlas.CenterRa, 2);
        Assert.True(await Eventually(() => atlas.Dsos.Any(d => d.Id == "M 42")), "the zoomed view fetched its deep-sky objects");
        Assert.True(await Eventually(() => atlas.Pointers.Contains("eq")));
        atlas.SelectedPointer = "eq";
        await atlas.GotoCommand.ExecuteAsync(null);
        Assert.Contains("going to M 42", atlas.Message);
        Assert.True(await Eventually(() => atlas.Markers.Any(m => !m.IsSelection && Math.Abs(m.RaHours - 5.588) < 0.05 && Math.Abs(m.DecDegrees + 5.39) < 0.3), 120000),
            "the mount's reticle should arrive on M 42");
        atlas.SearchText = "Jupiter";
        await atlas.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Planet", atlas.Results.First().Kind);
        atlas.SearchText = "M42";
        await atlas.SearchCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => atlas.Selection?.Label == "M 42"));
        atlas.ImageThisCommand.Execute(null);
        Assert.Equal("M42", vm.Image.Label);
        Assert.True(vm.Image.Width > 1);   // a big nebula becomes an area
        Assert.True(await Eventually(() => vm.Sky.Frame is { WidthDegrees: > 1 } f && f.Label == "M42"), "the frame on the chart follows the image");
        // a click right on Betelgeuse picks it
        atlas.CenterRa = 5.92; atlas.CenterDec = 7.4; atlas.Fov = 20;
        await atlas.RefreshAsync();
        Assert.True(await Eventually(() => atlas.Stars.Any(s => s.Label == "Betelgeuse")));
        atlas.PickCommand.Execute((5.9195, 7.4071, 40.0));
        Assert.Equal("Betelgeuse", atlas.Selection!.Label);
        Shot(window, "07e-atlas");

        // centring: configure through the UI, solve a guide frame (the simulator has no optics set here, so it may not solve: the log says either way)
        var centering = vm.Centering;
        Assert.True(await Eventually(() => centering.Mounts.Contains("Telescope_Simulator") && centering.Shooters.Contains("cam")));
        centering.MountId = "Telescope_Simulator"; centering.GuideShooter = "cam"; centering.PrimaryShooter = "cam"; centering.Enabled = false; centering.ExposureSeconds = 1;
        await centering.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("saved", centering.Message);
        if (solveSvc is not null)
        {
            await centering.SolveGuideCommand.ExecuteAsync(null);
            Assert.True(await Eventually(() => centering.Solves.Count >= 1, 120000), centering.Message);
        }
        vm.Navigate(AppView.Scopes);
        Shot(window, "07f-centring");
        if (solveSvc is not null) await solveSvc.DisposeAsync();

        // live stack: the Image tab's field (M42 from the atlas), frames placed by the scope's pointing, shown as they come
        var live = vm.LiveStack;
        Assert.True(await Eventually(() => live.Shooters.Any(c => c.Id == "main")));
        live.FromImageCommand.Execute(null);
        Assert.Equal("M42", live.Label);
        Assert.True(live.Shooters.First(c => c.Id == "main").Selected);
        live.PixelScale = 20; live.Registration = "Pointing"; live.FramePixelScale = 10;
        Assert.Contains("px", live.SizeText);
        await live.StartStackCommand.ExecuteAsync(null);
        Assert.Equal("", live.Message);
        Assert.True(await Eventually(() => live.Phase == "Stacking"));
        scope.TargetRa = "05:35:17"; scope.TargetDec = "-05:23:28"; scope.Count = 1; scope.Filter = "";
        await scope.ObserveCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => live.Summary.StartsWith("1 frames"), 120000), $"{live.Summary} | {live.StateMessage}");
        Assert.True(await Eventually(() => live.Preview.Image is not null, 30000), live.Preview.Info);
        await live.StopCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => live.Phase == "Stopped"));
        vm.Navigate(AppView.Advanced, "Manual live stack");
        Shot(window, "07g-live-stack");

        // guiding: a guide camera and the mount's guide port make a guider; a scope that owns it guides by itself
        Assert.True(await Eventually(() => vm.Catalog.OfKind(DeviceKinds.GuidePort).Any(d => d.Id == "Telescope_Simulator") && vm.Catalog.OfKind(DeviceKinds.Camera).Any(d => d.Id == "Guide_Simulator")));
        Assert.True((await Commands.CallAsync(bridge, EquipmentIds.Command(DeviceKinds.Camera, "Guide_Simulator", "Connect"), (BinaryConvertibleBool)true)).Ok.Value);
        vm.Navigate(AppView.Rig, "Set up");
        // a second train on the same mount, used as the guide scope
        Assert.True(await Eventually(() => composer.TrainCameras.Any(c => c.CameraId == "Guide_Simulator") && composer.GuidePorts.Contains("Telescope_Simulator")));
        composer.NewTrainCommand.Execute(null);
        composer.TrainName = "guidescope"; composer.FocalLength = 400; composer.SelectedWheel = "";
        foreach (var c in composer.TrainCameras) c.Role = c.CameraId == "Guide_Simulator" ? "Imaging" : ComposerViewModel.NotInTrain;
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => composer.GuideWith.Any(g => g.Id == "guidescope") && composer.Trains.Any(t => t.Id == "guidescope")), composer.TrainMessage);
        composer.NewScopeCommand.Execute(null);
        composer.ScopeName = "guided";
        composer.SelectedGuideWith = "guidescope"; composer.SelectedGuideOutput = "Pulse"; composer.SelectedGuidePort = ComposerViewModel.MountPort; composer.GuideExposure = 1;
        Assert.True(composer.Guided && composer.PulseOutput);
        composer.DitherEvery = 2;
        foreach (var c in composer.PointerChoices) c.IsSelected = c.Id == "eq";
        foreach (var c in composer.ShooterChoices) c.IsSelected = c.Id == "cam";
        await composer.SaveScopeCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => vm.Catalog.Scopes.Any(s => s.Id == "guided")), composer.ScopeMessage);
        Assert.Equal("guidescope", vm.Catalog.Composition.Scopes.First(s => s.Id.Text == "guided").GuideShooterId.Text);
        Shot(window, "07h-compose-guider");
        Assert.True(await Eventually(() => vm.Scopes.Cards.Any(c => c.ScopeId == "guided")));
        vm.Scopes.Select("guided"); vm.Navigate(AppView.Scopes);
        var guided = Assert.IsType<ScopePanelViewModel>(vm.Scopes.Selected);
        Assert.True(guided.HasGuider);
        await guided.StartGuidingCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => guided.GuidePhase is not ("--" or "Idle"), 30000), guided.GuidePhase);
        Shot(window, "07i-scope-guiding");
        await guided.StopGuidingCommand.ExecuteAsync(null);
        Assert.True(await Eventually(() => guided.GuidePhase == "Idle", 30000), guided.GuidePhase);

        // profiles: the installed drivers can be searched and put into a profile (not started here: the test runs its own server)
        var profiles = vm.Profiles;
        await profiles.StartAsync();
        profiles.Search = "simulator telescope";
        Assert.True(await Eventually(() => profiles.Matches.Any(m => m.Executable == "indi_simulator_telescope")), $"{profiles.Matches.Count} matches");
        profiles.SelectedMatch = profiles.Matches.First(m => m.Executable == "indi_simulator_telescope");
        profiles.AddDriverCommand.Execute(null);
        profiles.Label = "UI rig"; profiles.Port = 7999;
        await profiles.SaveCommand.ExecuteAsync(null);
        Assert.Equal("profile 'UI rig' saved", profiles.Message);
        Assert.True(await Eventually(() => profiles.Profiles.Contains("UI rig")));
        vm.Navigate(AppView.Rig, "Drivers");
        Shot(window, "07j-profiles");

        // calibration: a master bias of the camera, listed in the library
        var cal = vm.Calibration;
        vm.Navigate(AppView.Rig, "Calibration");
        Assert.True(await Eventually(() => cal.Cameras.Contains("cam-CCD_Simulator")), string.Join(",", cal.Cameras));
        cal.SelectedCamera = "cam-CCD_Simulator"; cal.Kind = "Bias"; cal.Count = 3;
        await cal.CaptureCommand.ExecuteAsync(null);
        Assert.Equal("", cal.Message);
        Assert.True(await Eventually(() => cal.Masters.Any(m => m.Line.Contains("Bias")), 60000), cal.Status);
        Shot(window, "07k-calibration");

        // generic INDI browser
        vm.Navigate(AppView.Advanced, "INDI properties");
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
