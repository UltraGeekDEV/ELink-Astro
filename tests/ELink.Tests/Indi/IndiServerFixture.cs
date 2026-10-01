using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ELink.Tests.Indi;

/// <summary>Runs a real indiserver with the simulator drivers on a free port, in a throwaway HOME so the user's
/// ~/.indi config is untouched. Tests are skipped (not failed) when indiserver is not installed.</summary>
public sealed class IndiServerFixture : IAsyncLifetime
{
    public static readonly string[] Drivers =
    {
        "indi_simulator_telescope", "indi_simulator_ccd", "indi_simulator_focus", "indi_simulator_wheel",
        "indi_simulator_rotator", "indi_simulator_dome", "indi_simulator_weather", "indi_simulator_gps",
    };

    private Process? _process;
    private string _home = "";
    public int Port { get; private set; }
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        if (!File.Exists("/usr/bin/indiserver") && !File.Exists("/usr/local/bin/indiserver")) return;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _home = Path.Combine(Path.GetTempPath(), "elink-indi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        var psi = new ProcessStartInfo("indiserver") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(Port.ToString());
        foreach (var d in Drivers) psi.ArgumentList.Add(d);
        psi.Environment["HOME"] = _home;
        _process = Process.Start(psi)!;
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine(); _process.BeginOutputReadLine();
        for (int i = 0; i < 100; i++)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, Port); Available = true; return; }
            catch (SocketException) { await Task.Delay(100); }
        }
    }

    public Task DisposeAsync()
    {
        try { if (_process is { HasExited: false }) { _process.Kill(true); _process.WaitForExit(2000); } } catch { }
        try { if (_home != "") Directory.Delete(_home, true); } catch { }
        return Task.CompletedTask;
    }
}

[CollectionDefinition("indiserver")]
public class IndiServerCollection : ICollectionFixture<IndiServerFixture> { }
