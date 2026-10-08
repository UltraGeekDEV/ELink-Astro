using System.Net;
using Avalonia;
using Avalonia.Fonts.Inter;
using ELink.Atlas;
using ELink.Automation;
using ELink.Stellarium;
using ELink.Compose;
using ELink.Core;
using ELink.IndiBridge;
using ELink.UI;

// ELink station: INDI bridge + composition host + UI in one process, on one EVent node. The UI and the backend
// still only talk through EVent (here over the node's local loopback). The node also listens, so other ELink
// processes (another UI, a sequencer) can join this station's mesh.
//   elink [--web-port 8080] [--ws-port 8081] [--web-listen 127.0.0.1] [--no-web] [--profile NAME] [--indi host[:port][=name]]... [--port 5698] [--listen 127.0.0.1] [--compose file.json] [--save-dir DIR] [--save SHOOTER]... [--stellarium-port 10001] [--stellarium-remote URL] [--sky-data /usr/share/kstars] [--no-ui]
var indi = new List<(string Name, string Host, int Port)>();
int port = ElinkNode.DefaultPort; var listen = IPAddress.Loopback; bool ui = true;
string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ELink");
var save = new List<string>();
string? profile = null;
int webPort = 8080, wsPort = 8081; var webListen = IPAddress.Loopback; bool web = true;
int stellariumPort = 10001; string stellariumRemote = "http://127.0.0.1:8090"; string skyData = "/usr/share/kstars";
string compose = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "elink", "compose.json");
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--port": port = int.Parse(Next()); break;
        case "--listen": listen = IPAddress.Parse(Next()); break;
        case "--compose": compose = Next(); break;
        case "--no-ui": ui = false; break;
        case "--no-web": web = false; break;
        case "--web-port": webPort = int.Parse(Next()); break;
        case "--ws-port": wsPort = int.Parse(Next()); break;
        case "--web-listen": webListen = IPAddress.Parse(Next()); break;
        case "--save-dir": saveDir = Next(); break;
        case "--save": save.Add(Next()); break;
        case "--stellarium-port": stellariumPort = int.Parse(Next()); break;
        case "--stellarium-remote": stellariumRemote = Next(); break;
        case "--sky-data": skyData = Next(); break;
        case "--profile": profile = Next(); break;
        case "--indi":
            {
                string spec = Next(), name = "";
                int eq = spec.IndexOf('=');
                if (eq >= 0) { name = spec[(eq + 1)..]; spec = spec[..eq]; }
                int c = spec.LastIndexOf(':');
                string h0 = c > 0 && int.TryParse(spec[(c + 1)..], out var p) ? spec[..c] : spec;
                int ip = c > 0 && int.TryParse(spec[(c + 1)..], out var q) ? q : 7624;
                indi.Add((name != "" ? name : (indi.Count == 0 ? "indi" : $"indi{indi.Count + 1}"), h0, ip));
                break;
            }
        default:
            Console.WriteLine("usage: elink [--indi host[:port][=name]]... [--port 5698] [--listen 127.0.0.1] [--compose file.json] [--save-dir DIR] [--save SHOOTER]... [--stellarium-port 10001] [--stellarium-remote URL] [--sky-data DIR] [--profile NAME] [--web-port 8080] [--ws-port 8081] [--web-listen ADDR] [--no-web] [--no-ui]");
            return args[i] is "-h" or "--help" ? 0 : 1;
    }
}
// with a profile, ELink runs the drivers itself; otherwise it bridges an indiserver already running
if (indi.Count == 0 && profile is null) indi.Add(("indi", "localhost", 7624));

Directory.CreateDirectory(Path.GetDirectoryName(compose)!);
// the node also serves a WebSocket for the web interface's page (a JavaScript leaf); without it the station still runs
Event.CoreFunctionality.TypeSafeEVentNode node;
try { node = web ? ElinkNode.Create("Station", port, listen, WebGateway.Transport(webListen, wsPort)) : ElinkNode.Create("Station", port, listen); }   // created here, off any UI thread
catch (Exception ex) when (web) { Console.WriteLine($"web interface: not started ({ex.Message})"); web = false; node = ElinkNode.Create("Station", port, listen); }
using var nodeLife = node;
var directory = new DeviceDirectory(node);
await directory.StartAsync();
await using var profiles = new ProfileService(node, directory, Path.Combine(Path.GetDirectoryName(compose)!, "profiles.bin"));
await profiles.StartAsync();
if (profile is not null)
{
    var started = await Commands.CallAsync(node, ELink.Contracts.Equipment.ProfileIds.Start, (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)profile, TimeSpan.FromSeconds(60));
    Console.WriteLine(started.Ok.Value ? $"profile {profile}: running" : $"profile {profile}: {started.Error.Text}");
}
await using var host = new CompositionHost(node, compose);
await host.StartAsync();
await using var autofocus = new AutofocusService(node);
await autofocus.StartAsync();
await using var processing = new ProcessingService(node, Path.Combine(saveDir, "pictures"));
await processing.StartAsync();
await using var focusAssist = new FocusAssistService(node);
await focusAssist.StartAsync();
await using var scheduler = new SchedulerService(node, Path.Combine(Path.GetDirectoryName(compose)!, "schedule.bin"));
await scheduler.StartAsync();
PlateSolveService? solver = null;
var solvers = new List<IPlateSolver>();
if (AstapSolver.Locate() is { } astap) { solvers.Add(new AstapSolver(astap)); Console.WriteLine($"plate solving: ASTAP {astap}"); }
if (PlateSolver.Locate() is { } solveField) { solvers.Add(new PlateSolver(solveField)); Console.WriteLine($"plate solving: astrometry.net {solveField}"); }
if (solvers.Count > 0)
{
    solver = new PlateSolveService(node, solvers.ToArray());
    await solver.StartAsync();
}
else Console.WriteLine("plate solving: no solver found (install ASTAP with a star database, or astrometry.net; or set ELINK_ASTAP / ELINK_SOLVE_FIELD)");
await using var site = new SiteService(node, Path.Combine(Path.GetDirectoryName(compose)!, "site.json"));
await site.StartAsync();
await using var centering = new CenteringService(node, Path.Combine(Path.GetDirectoryName(compose)!, "centering.json"));
await centering.StartAsync();
await using var imaging = new ImagingService(node, Path.GetDirectoryName(compose)!);
await imaging.StartAsync();
await using var liveStack = new LiveStackService(node, Path.GetDirectoryName(compose)!);
await liveStack.StartAsync();
await using var calibration = new CalibrationService(node, Path.GetDirectoryName(compose)!);
await calibration.StartAsync();
await using var storage = new StorageService(node, saveDir);
await storage.StartAsync();
await using var sessionLog = new SessionLogService(node, Path.Combine(saveDir, "logs"));
await sessionLog.StartAsync();
foreach (var id in save) await Commands.CallAsync(node, ELink.Contracts.Automation.StorageIds.Watch, new ELink.Contracts.Automation.StorageWatch { ShooterId = id });
AtlasService? atlas = null;
TonightService? tonight = null;
if (File.Exists(Path.Combine(skyData, "namedstars.dat")))
{
    var catalog = await Task.Run(() => AtlasCatalog.LoadKStars(skyData));
    atlas = new AtlasService(node, catalog);
    await atlas.StartAsync();
    tonight = new TonightService(node, catalog);
    await tonight.StartAsync();
    Console.WriteLine($"sky atlas: {catalog.Stars.Count} stars, {catalog.AllDsos.Count} deep-sky objects" + (catalog.DeepStars is null ? "" : ", faint stars from the GSC"));
}
else Console.WriteLine($"sky atlas: no KStars sky data in {skyData} (install kstars-data, or pass --sky-data)");
await using var stellarium = new StellariumService(node, stellariumPort, stellariumRemote);
await stellarium.StartAsync();
Console.WriteLine($"Stellarium: telescope server on port {stellarium.Telescope.Port} (J2000), remote control at {stellariumRemote}");
var links = new List<IndiServerLink>();
foreach (var (name, h, p) in indi)
{
    var link = new IndiServerLink(node, directory, name, h, p);
    await link.StartAsync();
    links.Add(link);
    Console.WriteLine($"indi '{name}' -> {h}:{p}");
}
Console.WriteLine($"station mesh on {listen}:{port}; compositions in {compose}");
WebGateway? gateway = null;
if (web)
{
    if (WebGateway.FindSite() is { } webRoot)
    {
        try { gateway = new WebGateway(node, webRoot, webListen, webPort, wsPort); Console.WriteLine($"web interface: http://{(webListen.Equals(IPAddress.Any) ? "localhost" : webListen.ToString())}:{webPort}/" + (IPAddress.IsLoopback(webListen) ? "" : "  (open to the network: no login!)")); }
        catch (Exception ex) { Console.WriteLine($"web interface: not started ({ex.Message})"); }
    }
    else Console.WriteLine("web interface: the web folder is missing next to the program");
}

int exit = 0;
if (ui)
{
    ELink.UI.App.Options = new UiLaunchOptions { Node = node };
    exit = AppBuilder.Configure<ELink.UI.App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
}
else
{
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    await stop.Task;
}
gateway?.Dispose();
foreach (var l in links) await l.DisposeAsync();
if (atlas is not null) await atlas.DisposeAsync();
if (tonight is not null) await tonight.DisposeAsync();
if (solver is not null) await solver.DisposeAsync();
return exit;
