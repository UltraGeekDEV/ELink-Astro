using System.Net;
using ELink.Core;
using ELink.IndiBridge;

// ELink bridge: connects to one or more INDI servers and offers their equipment on the EVent mesh.
//   elink-bridge [--name Bridge] [--port 5698] [--listen 127.0.0.1] [--indi host[:port][=name]]... [--join host:port]
var opt = Options.Parse(args);
if (opt.Help) { Console.WriteLine(Options.Usage); return 0; }

using var node = ElinkNode.Create(opt.Name, opt.Port, opt.Listen);
var directory = new DeviceDirectory(node);
await directory.StartAsync();

var links = new List<IndiServerLink>();
foreach (var (name, host, port) in opt.IndiServers)
{
    var link = new IndiServerLink(node, directory, name, host, port);
    await link.StartAsync();
    links.Add(link);
    Console.WriteLine($"indi '{name}' -> {host}:{port}");
}
if (links.Count == 0) Console.WriteLine("no --indi given; nothing to bridge (try --indi localhost)");

foreach (var peer in opt.Join)
{
    var result = await ElinkNode.JoinAsync(node, peer.Host, peer.Port);
    Console.WriteLine($"join {peer.Host}:{peer.Port}: {result}");
}

Console.WriteLine($"bridge '{opt.Name}' listening on {opt.Listen}:{opt.Port}. Ctrl-C to stop.");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
foreach (var l in links) await l.DisposeAsync();
return 0;

internal sealed class Options
{
    public const string Usage =
        "usage: elink-bridge [--name Bridge] [--port 5698] [--listen 127.0.0.1] [--indi host[:port][=name]]... [--join host:port]...";
    public bool Help;
    public string Name = "Bridge";
    public int Port = ElinkNode.DefaultPort;
    public IPAddress Listen = IPAddress.Loopback;
    public List<(string Name, string Host, int Port)> IndiServers = new();
    public List<(string Host, int Port)> Join = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "-h" or "--help": o.Help = true; break;
                case "--name": o.Name = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--listen": o.Listen = IPAddress.Parse(Next()); break;
                case "--indi":
                    {
                        string spec = Next(), name = "";
                        int eq = spec.IndexOf('=');
                        if (eq >= 0) { name = spec[(eq + 1)..]; spec = spec[..eq]; }
                        var (host, port) = HostPort(spec, 7624);
                        o.IndiServers.Add((name != "" ? name : (o.IndiServers.Count == 0 ? "indi" : $"indi{o.IndiServers.Count + 1}"), host, port));
                        break;
                    }
                case "--join": { var (h, p) = HostPort(Next(), ElinkNode.DefaultPort); o.Join.Add((h, p)); break; }
                default: throw new ArgumentException($"unknown option {args[i]}\n{Usage}");
            }
        }
        return o;
    }

    private static (string, int) HostPort(string s, int defaultPort)
    {
        int c = s.LastIndexOf(':');
        return c > 0 && int.TryParse(s[(c + 1)..], out var p) ? (s[..c], p) : (s, defaultPort);
    }
}
