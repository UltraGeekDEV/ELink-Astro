using System.Diagnostics;
using System.Xml.Linq;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge;

/// <summary>Equipment profiles: ELink runs the rig's INDI drivers itself, as Ekos does. A profile is a list of drivers;
/// starting it starts an indiserver with a control pipe, starts each driver through it, links the server to the mesh
/// (the devices appear as ELink equipment) and connects the devices. If the indiserver dies it is started again.</summary>
public sealed class ProfileService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly DeviceDirectory _directory;
    private readonly string? _file;
    private readonly string _driverDir;
    private readonly CommandSet _commands;
    private readonly StatePublisher<ProfileState> _publisher;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _switch = new(1, 1);
    private EquipmentProfiles _profiles = new();
    private string _running = "", _phase = "Stopped", _message = "";
    private int _port, _restarts;
    private List<string> _drivers = new();
    private Process? _server;
    private string? _work;
    private IndiServerLink? _link;
    private IndiClient? _connector;
    private CancellationTokenSource? _watch;

    /// <summary>How many times a dying indiserver is started again before giving up.</summary>
    public int MaxRestarts { get; set; } = 10;

    /// <param name="file">where profiles are kept; null = in memory</param>
    /// <param name="driverDir">INDI's driver catalogue (*.xml)</param>
    public ProfileService(TypeSafeEVentNode node, DeviceDirectory directory, string? file = null, string driverDir = "/usr/share/indi")
    {
        _node = node; _directory = directory; _file = file; _driverDir = driverDir;
        _commands = new CommandSet(node);
        _publisher = new(node, ProfileIds.State, ProfileIds.GetState, BuildState);
        if (file is not null && File.Exists(file))
        {
            try { Span<byte> b = File.ReadAllBytes(file); var p = new EquipmentProfiles(); p.FromBytes(ref b); _profiles = p; }
            catch (Exception ex) { _message = "could not read the profiles: " + ex.Message; }
        }
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<NOTESVoid, EquipmentProfiles>(ProfileIds.List, _ => Task.FromResult(CopyProfiles()), "the equipment profiles");
        await _commands.AddAsync<EquipmentProfile, CommandResult>(ProfileIds.Save, p => Task.FromResult(Save(p)), "create or replace a profile");
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(ProfileIds.Delete, l => Task.FromResult(Delete(l.Text)), "delete a profile");
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(ProfileIds.Start, l => StartProfileAsync(l.Text), "run a profile's drivers");
        await _commands.AddAsync<NOTESVoid, CommandResult>(ProfileIds.Stop, async _ => { await StopProfileAsync(); return CommandResult.Success(); }, "stop the running profile");
        await _commands.AddAsync<NOTESVoid, DriverCatalog>(ProfileIds.Catalog, _ => Task.FromResult(Catalog()), "the INDI drivers installed");
        await _publisher.StartAsync();
    }

    // ---- profiles ----------------------------------------------------------------------------------------------

    private EquipmentProfiles CopyProfiles() { lock (_gate) { Span<byte> b = _profiles.ToBytes(); var c = new EquipmentProfiles(); c.FromBytes(ref b); return c; } }

    private CommandResult Save(EquipmentProfile p)
    {
        string label = p.Label.Text.Trim();
        if (label == "") return CommandResult.Fail("a profile needs a name");
        if (p.Drivers.Count == 0) return CommandResult.Fail("a profile needs at least one driver");
        if (p.Drivers.Any(d => d.Text.Trim() == "" || d.Text.Any(char.IsWhiteSpace))) return CommandResult.Fail("driver names are executables, without spaces");
        if (p.Port.Value is < 1 or > 65535) return CommandResult.Fail("the port must be 1..65535");
        lock (_gate)
        {
            var keep = _profiles.Profiles.Where(x => !string.Equals(x.Label.Text, label, StringComparison.OrdinalIgnoreCase)).ToList();
            _profiles = new EquipmentProfiles();
            foreach (var x in keep) _profiles.Profiles.Add(x);
            _profiles.Profiles.Add(p);
        }
        return Persist();
    }

    private CommandResult Delete(string label)
    {
        lock (_gate)
        {
            if (string.Equals(_running, label, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("that profile is running: stop it first");
            var keep = _profiles.Profiles.Where(x => !string.Equals(x.Label.Text, label, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keep.Count == _profiles.Profiles.Count) return CommandResult.Fail($"no profile '{label}'");
            _profiles = new EquipmentProfiles();
            foreach (var x in keep) _profiles.Profiles.Add(x);
        }
        return Persist();
    }

    private CommandResult Persist()
    {
        if (_file is null) return CommandResult.Success();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            byte[] bytes; lock (_gate) bytes = _profiles.ToBytes();
            File.WriteAllBytes(_file + ".part", bytes); File.Move(_file + ".part", _file, true);
            return CommandResult.Success();
        }
        catch (Exception ex) { return CommandResult.Fail("could not keep the profiles: " + ex.Message); }
    }

    /// <summary>The drivers INDI has installed: every devGroup/device/driver in its catalogue files.</summary>
    public DriverCatalog Catalog()
    {
        var cat = new DriverCatalog();
        if (!Directory.Exists(_driverDir)) return cat;
        var seen = new HashSet<(string, string)>();
        foreach (var file in Directory.GetFiles(_driverDir, "*.xml").Order())
        {
            try
            {
                var doc = XDocument.Load(file);
                foreach (var group in doc.Descendants("devGroup"))
                    foreach (var dev in group.Elements("device"))
                    {
                        string exec = dev.Element("driver")?.Value.Trim() ?? "";
                        string label = dev.Attribute("label")?.Value ?? "";
                        if (exec == "" || !seen.Add((label, exec))) continue;
                        cat.Drivers.Add(new DriverEntry { Group = group.Attribute("group")?.Value ?? "", Device = label, Manufacturer = dev.Attribute("manufacturer")?.Value ?? "", Executable = exec });
                    }
            }
            catch (Exception) { /* a broken third-party file: skip it */ }
        }
        return cat;
    }

    // ---- running a profile -----------------------------------------------------------------------------------

    private async Task<CommandResult> StartProfileAsync(string label)
    {
        EquipmentProfile? p;
        lock (_gate) p = _profiles.Profiles.FirstOrDefault(x => string.Equals(x.Label.Text, label, StringComparison.OrdinalIgnoreCase));
        if (p is null) return CommandResult.Fail($"no profile '{label}'");
        await StopProfileAsync();
        await _switch.WaitAsync();
        try
        {
            lock (_gate)
            {
                _running = p.Label.Text; _phase = "Starting"; _port = p.Port.Value; _restarts = 0; _message = "";
                _drivers = p.Drivers.Select(d => d.Text.Trim()).ToList();
            }
            await Publish();
            string error = await LaunchAsync(p);
            if (error != "") { lock (_gate) { _phase = "Error"; _message = error; } await Publish(); return CommandResult.Fail(error); }
            // the devices onto the mesh, and connected
            _link = new IndiServerLink(_node, _directory, "profile-" + EquipmentIds.Segment(p.Label.Text), "127.0.0.1", p.Port.Value);
            await _link.StartAsync();
            if (p.AutoConnect.Value)
            {
                _connector = new IndiClient("127.0.0.1", p.Port.Value);
                _connector.Changed += ConnectDevicesAsync;
                await _connector.ConnectAsync();
                await _connector.GetPropertiesAsync();
            }
            _watch = new CancellationTokenSource();
            var ct = _watch.Token;
            _ = Task.Run(() => WatchAsync(p, ct));
            lock (_gate) _phase = "Running";
            await Publish();
            return CommandResult.Success();
        }
        finally { _switch.Release(); }
    }

    /// <summary>indiserver with a control pipe and its own socket, then "start &lt;driver&gt;" for each driver.</summary>
    private async Task<string> LaunchAsync(EquipmentProfile p)
    {
        _work ??= Path.Combine(Path.GetTempPath(), "elink-indi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        string fifo = Path.Combine(_work, "control");
        if (!File.Exists(fifo))
        {
            using var mk = Process.Start(new ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false });
            if (mk is null) return "could not make the control pipe (mkfifo)";
            await mk.WaitForExitAsync();
            if (mk.ExitCode != 0) return "could not make the control pipe (mkfifo)";
        }
        var psi = new ProcessStartInfo("indiserver") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-p", p.Port.Value.ToString(), "-f", fifo, "-u", Path.Combine(_work, "sock"), "-r", "5" }) psi.ArgumentList.Add(a);
        try { _server = Process.Start(psi); }
        catch (Exception ex) { return "could not start indiserver: " + ex.Message; }
        if (_server is null) return "could not start indiserver";
        _server.OutputDataReceived += (_, _) => { }; _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine(); _server.BeginErrorReadLine();
        // listening?
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !_server.HasExited)
        {
            try { using var tcp = new System.Net.Sockets.TcpClient(); await tcp.ConnectAsync("127.0.0.1", p.Port.Value); break; }
            catch { await Task.Delay(100); }
        }
        if (_server.HasExited) return $"indiserver stopped at once (is port {p.Port.Value} in use?)";
        // drivers through the pipe (opening it waits until indiserver reads it)
        try
        {
            using var pipe = new StreamWriter(new FileStream(fifo, FileMode.Open, FileAccess.Write)) { AutoFlush = true };
            foreach (var d in p.Drivers) await pipe.WriteLineAsync("start " + d.Text.Trim());
        }
        catch (Exception ex) { return "could not start the drivers: " + ex.Message; }
        return "";
    }

    /// <summary>Connect every device the server shows (once per start).</summary>
    private async Task ConnectDevicesAsync(IndiChange change)
    {
        if (change is PropertyDefined { Property: { Name: "CONNECTION" } prop } && prop.Switch("CONNECT") != true && _connector is { } c)
            try { await c.SetSwitchAsync(prop.Device, "CONNECTION", "CONNECT"); } catch (Exception) { }
    }

    /// <summary>A dying indiserver is started again (with its drivers), up to MaxRestarts times.</summary>
    private async Task WatchAsync(EquipmentProfile p, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _server!.WaitForExitAsync(ct); } catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            int n; lock (_gate) { n = ++_restarts; _phase = "Restarting"; _message = $"indiserver stopped: starting it again ({n})"; }
            await Publish();
            if (n > MaxRestarts) { lock (_gate) { _phase = "Error"; _message = "indiserver keeps stopping: given up"; } await Publish(); return; }
            await Task.Delay(1000, ct);
            string error = await LaunchAsync(p);
            lock (_gate) { _phase = error == "" ? "Running" : "Error"; _message = error == "" ? $"started again ({n})" : error; }
            await Publish();
            if (error != "") return;
            // the old connection went down with the server: connect the devices again through a new one
            if (_connector is not null)
            {
                _connector.Changed -= ConnectDevicesAsync;
                try { await _connector.DisposeAsync(); } catch (Exception) { }
                _connector = new IndiClient("127.0.0.1", p.Port.Value);
                _connector.Changed += ConnectDevicesAsync;
                try { await _connector.ConnectAsync(); await _connector.GetPropertiesAsync(); } catch (Exception) { }
            }
        }
    }

    private async Task StopProfileAsync()
    {
        await _switch.WaitAsync();
        try
        {
            _watch?.Cancel(); _watch = null;
            if (_connector is not null) { _connector.Changed -= ConnectDevicesAsync; await _connector.DisposeAsync(); _connector = null; }
            if (_link is not null) { await _link.DisposeAsync(); _link = null; }
            if (_server is { HasExited: false } s)
            {
                try { s.Kill(true); await s.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
            _server?.Dispose(); _server = null;
            // indiserver's drivers are its children; anything left over goes with the folder's socket
            lock (_gate) { _running = ""; _phase = "Stopped"; _drivers = new(); }
        }
        finally { _switch.Release(); }
        await Publish();
    }

    private async Task Publish() { try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { } }

    private ProfileState BuildState()
    {
        lock (_gate)
        {
            var s = new ProfileState { Running = _running, Phase = _phase, Port = _port, Restarts = _restarts, Message = _message };
            foreach (var d in _drivers) s.Drivers.Add(d);
            return s;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopProfileAsync();
        _commands.Dispose(); _publisher.Dispose();
        if (_work is not null) { try { Directory.Delete(_work, true); } catch { } }
    }
}
