using Avalonia.Headless;
using Avalonia.Threading;
using ELink.Atlas;
using ELink.Automation;
using ELink.Compose;
using ELink.IndiBridge;
using ELink.UI.Infrastructure;
using ELink.UI.ViewModels;
using ELink.UI.Views;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The whole simulated backend (bridge, composition host, every service, each its own EVent node where the real
/// station has them) with the UI joined to it from a separate node, rendered headless. Tests drive the view models and take
/// pictures; set ELINK_SHOT_HEIGHT to render pages taller than a laptop screen.</summary>
public sealed class UiRig : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _services = new();
    private readonly List<IDisposable> _nodes = new();
    private readonly string _saveDir = Path.Combine(Path.GetTempPath(), "elink-ui-save-" + Guid.NewGuid().ToString("N"));

    public MeshSession Session { get; private set; } = null!;
    public MainViewModel Vm { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;

    public static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    public static string ShotDir()
    {
        var dir = Environment.GetEnvironmentVariable("ELINK_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "elink-screens");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Shot(string name)
    {
        Dispatcher.UIThread.RunJobs();
        Window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir(), name + ".png"));
    }

    public static async Task<UiRig> StartAsync(IndiServerFixture server)
    {
        var rig = new UiRig();
        int bridgePort = ELink.Testing.TestPorts.Next();
        var bridge = await Task.Run(() => ElinkNode.Create("UI-Bridge", bridgePort));
        rig._nodes.Add(bridge);
        var dir = new DeviceDirectory(bridge); await dir.StartAsync();
        rig.Add(new IndiServerLink(bridge, dir, "sim", "127.0.0.1", server.Port)); await ((IndiServerLink)rig._services[^1]).StartAsync();
        var hostNode = await Task.Run(() => ElinkNode.Create("UI-Host", ELink.Testing.TestPorts.Next()));
        rig._nodes.Add(hostNode);
        await ElinkNode.JoinAsync(hostNode, "127.0.0.1", bridgePort);
        var host = rig.Add(new CompositionHost(hostNode)); await host.StartAsync();
        await rig.Add(new AutofocusService(hostNode)).StartAsync();
        await rig.Add(new SchedulerService(hostNode)).StartAsync();
        await rig.Add(new ProfileService(hostNode, new DeviceDirectory(hostNode))).StartAsync();
        await rig.Add(new ImagingService(hostNode)).StartAsync();
        var skyCatalog = File.Exists("/usr/share/kstars/namedstars.dat") ? await Task.Run(() => AtlasCatalog.LoadKStars())
            : AtlasCatalog.From(new List<CatalogStar> { new(5.92, 7.41, 0.45f, 1.85f, 39801, "Betelgeuse") }, new List<CatalogDso> { new("M 42", "Nebula", "Orion Nebula, NGC 1976", 5.588, -5.39, 4f, 90, 60, 0) });
        await rig.Add(new AtlasService(hostNode, skyCatalog)).StartAsync();
        await rig.Add(new CenteringService(hostNode)).StartAsync();
        await rig.Add(new LiveStackService(hostNode)).StartAsync();
        await rig.Add(new SiteService(hostNode)).StartAsync();
        if (PlateSolver.Locate() is { } sf) await rig.Add(new PlateSolveService(hostNode, new PlateSolver(sf))).StartAsync();
        await rig.Add(new StorageService(hostNode, rig._saveDir)).StartAsync();
        await rig.Add(new CalibrationService(hostNode, rig._saveDir)).StartAsync();

        // the UI: a separate node that joins the mesh
        rig.Session = await MeshSession.CreateAsync(null, "UI-Test");
        rig.Vm = new MainViewModel(rig.Session) { Port = bridgePort };
        int height = int.TryParse(Environment.GetEnvironmentVariable("ELINK_SHOT_HEIGHT"), out var sh) && sh >= 600 ? sh : 820;
        int width = int.TryParse(Environment.GetEnvironmentVariable("ELINK_SHOT_WIDTH"), out var sw) && sw >= 900 ? sw : 1280;
        rig.Window = new MainWindow { DataContext = rig.Vm, Width = width, Height = height };
        rig.Window.Show();
        await rig.Vm.ConnectCommand.ExecuteAsync(null);
        await rig.Vm.StartAsync();
        return rig;
    }

    /// <summary>The usual simulated rig: a pointer on the telescope simulator, a main train (CCD, filter wheel, focuser), a guide
    /// scope train, and a scope that guides with it. Returns once the UI has seen the scope.</summary>
    public async Task DefineSimulatedRigAsync(string scopeId = "main", bool guided = true)
    {
        var node = Session.Node;
        var ok = async (Task<ELink.Contracts.Equipment.CommandResult> t) => { var r = await t; Assert.True(r.Ok.Value, r.Error.Text); };
        await ok(Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" }));
        var main = new ImagingTrainDefinition { Id = "cam", Label = "main train", FocalLengthMm = 400, ApertureMm = 80, FilterWheelId = "Filter_Simulator", FocuserId = "Focuser_Simulator" };
        main.Cameras.Add(new TrainCamera { CameraId = "CCD_Simulator", Role = "Imaging" });
        await ok(Commands.CallAsync(node, ScopeIds.DefineTrain, main));
        if (guided)
        {
            var guide = new ImagingTrainDefinition { Id = "guidescope", Label = "guide scope", FocalLengthMm = 400, ApertureMm = 80 };
            guide.Cameras.Add(new TrainCamera { CameraId = "Guide_Simulator", Role = "Imaging" });
            await ok(Commands.CallAsync(node, ScopeIds.DefineTrain, guide));
        }
        var scope = new ScopeDefinition { Id = scopeId, DisplayName = "Main scope", GuideShooterId = guided ? "guidescope" : "", GuideExposureSeconds = 1, MeridianFlip = false };
        scope.Pointers.Add("eq"); scope.Shooters.Add(new ScopeShooterRef { Id = "cam" });
        await ok(Commands.CallAsync(node, ScopeIds.Define, scope));
        Assert.True(await Eventually(() => Vm.Catalog.Scopes.Any(x => x.Id == scopeId) && Vm.Scopes.Cards.Any(c => c.ScopeId == scopeId)), "the UI did not see the scope");
    }

    private T Add<T>(T service) where T : IAsyncDisposable { _services.Add(service); return service; }

    public async ValueTask DisposeAsync()
    {
        try { Vm.Dispose(); Window.Close(); } catch { }
        for (int i = _services.Count - 1; i >= 0; i--) { try { await _services[i].DisposeAsync(); } catch { } }
        foreach (var n in _nodes) { try { n.Dispose(); } catch { } }
        try { Directory.Delete(_saveDir, true); } catch { }
    }
}
