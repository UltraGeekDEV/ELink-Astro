using System.Net;
using Avalonia;
using Avalonia.Fonts.Inter;
using ELink.Automation;
using ELink.Compose;
using ELink.Core;
using ELink.IndiBridge;
using ELink.UI;

// ELink station: INDI bridge + composition host + UI in one process, on one EVent node. The UI and the backend
// still only talk through EVent (here over the node's local loopback). The node also listens, so other ELink
// processes (another UI, a sequencer) can join this station's mesh.
//   elink [--indi host[:port][=name]]... [--port 5698] [--listen 127.0.0.1] [--compose file.json] [--save-dir DIR] [--save SHOOTER]... [--no-ui]
var indi = new List<(string Name, string Host, int Port)>();
int port = ElinkNode.DefaultPort; var listen = IPAddress.Loopback; bool ui = true;
string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ELink");
var save = new List<string>();
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
        case "--save-dir": saveDir = Next(); break;
        case "--save": save.Add(Next()); break;
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
            Console.WriteLine("usage: elink [--indi host[:port][=name]]... [--port 5698] [--listen 127.0.0.1] [--compose file.json] [--save-dir DIR] [--save SHOOTER]... [--no-ui]");
            return args[i] is "-h" or "--help" ? 0 : 1;
    }
}
if (indi.Count == 0) indi.Add(("indi", "localhost", 7624));

Directory.CreateDirectory(Path.GetDirectoryName(compose)!);
using var node = ElinkNode.Create("Station", port, listen);   // created here, off any UI thread
var directory = new DeviceDirectory(node);
await directory.StartAsync();
await using var host = new CompositionHost(node, compose);
await host.StartAsync();
await using var autofocus = new AutofocusService(node);
await autofocus.StartAsync();
await using var sequencer = new SequencerService(node);
await sequencer.StartAsync();
await using var storage = new StorageService(node, saveDir);
await storage.StartAsync();
foreach (var id in save) await Commands.CallAsync(node, ELink.Contracts.Automation.StorageIds.Watch, new ELink.Contracts.Automation.StorageWatch { ShooterId = id });
var links = new List<IndiServerLink>();
foreach (var (name, h, p) in indi)
{
    var link = new IndiServerLink(node, directory, name, h, p);
    await link.StartAsync();
    links.Add(link);
    Console.WriteLine($"indi '{name}' -> {h}:{p}");
}
Console.WriteLine($"station mesh on {listen}:{port}; compositions in {compose}");

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
foreach (var l in links) await l.DisposeAsync();
return exit;
