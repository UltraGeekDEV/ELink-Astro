using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ELink.Core;
using ELink.Stellarium;
using ELink.Tests.Compose;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests.Atlas;

/// <summary>Against a real Stellarium, started headless (Qt offscreen) with a throwaway profile: the user's own Stellarium and
/// settings are not touched. Only runs when ELINK_LIVE_STELLARIUM=1 (it takes about a minute and needs Stellarium installed).</summary>
public class StellariumLiveTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ELINK_LIVE_STELLARIUM") == "1" && File.Exists("/usr/bin/stellarium");
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private static Process Launch(string profile) =>
        Process.Start(new ProcessStartInfo("stellarium") { ArgumentList = { "--user-dir", profile }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, Environment = { ["QT_QPA_PLATFORM"] = "offscreen" } })!;

    private static void Stop(Process p) { try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { } }

    [Fact]
    public async Task StellariumShowsAndSlewsAnElinkPointer()
    {
        if (!Enabled) { output.WriteLine("set ELINK_LIVE_STELLARIUM=1 to run"); return; }
        string profile = Path.Combine(Path.GetTempPath(), "elink-stel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        int remotePort = FreePort(), telescopePort = FreePort();
        try
        {
            // 1. let Stellarium write its own default configuration, then switch on the two plugins
            var first = Launch(profile);
            string cfgFile = Path.Combine(profile, "config.ini");
            // wait until Stellarium has written its plugin list (it does so once the plugins are loaded)
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                try { if (File.Exists(cfgFile) && Regex.IsMatch(File.ReadAllText(cfgFile), @"(?m)^TelescopeControl\s*=")) break; } catch (IOException) { }
            }
            await Task.Delay(1000); Stop(first);
            string cfg = File.ReadAllText(cfgFile);
            cfg = Regex.Replace(cfg, @"(?m)^RemoteControl\s*=.*$", "RemoteControl = true");
            cfg = Regex.Replace(cfg, @"(?m)^TelescopeControl\s*=.*$", "TelescopeControl = true");
            if (!Regex.IsMatch(cfg, @"(?m)^RemoteControl\s*=\s*true")) cfg += "\n[plugins_load_at_startup]\nRemoteControl = true\nTelescopeControl = true\n";
            cfg = Regex.Replace(cfg, @"(?ms)^\[RemoteControl\].*?(?=^\[|\z)", "");
            cfg += $"\n[RemoteControl]\nautostart = true\nenable_authentication = false\nport = {remotePort}\n";
            File.WriteAllText(Path.Combine(profile, "config.ini"), cfg);
            Directory.CreateDirectory(Path.Combine(profile, "modules", "TelescopeControl"));
            File.WriteAllText(Path.Combine(profile, "modules", "TelescopeControl", "telescopes.json"),
                $$"""{"1":{"connection":"remote","equinox":"J2000","host_name":"localhost","name":"ELink","tcp_port":{{telescopePort}},"delay":500000,"connect_at_startup":true,"circles":[]},"version":"0.4.1"}""");

            // 2. ELink: a pointer at Betelgeuse, served as a Stellarium telescope
            using var node = ElinkNode.Create("SL-" + Guid.NewGuid().ToString("N")[..6], FreePort());
            await using var pointer = new FakePointer(node, "live", 20); await pointer.StartAsync();
            await ELink.Core.Commands.CallAsync(node, ELink.Contracts.Composition.PointerIds.Goto("live"), new ELink.Contracts.Equipment.SkyTarget { RaHours = 5.9195, DecDegrees = 7.407, Epoch = "J2000" });
            await using var server = new StellariumTelescopeServer(node, telescopePort); await server.StartAsync("live");

            var stel = Launch(profile);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remotePort}/"), Timeout = TimeSpan.FromSeconds(3) };
                bool up = false;
                for (int i = 0; i < 120 && !up; i++) { try { up = (await http.GetAsync("api/main/status")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
                Assert.True(up, "Stellarium Remote Control did not come up");
                for (int i = 0; i < 60 && server.ClientCount == 0; i++) await Task.Delay(500);
                Assert.True(server.ClientCount > 0, "Stellarium did not connect to the ELink telescope server");

                // 3. Stellarium shows the telescope where the pointer is
                JsonElement info = default; bool found = false;
                for (int i = 0; i < 30 && !found; i++)
                {
                    var text = await http.GetStringAsync("api/objects/info?name=ELink&format=json");
                    if (text.TrimStart().StartsWith('{')) { info = JsonDocument.Parse(text).RootElement; found = info.TryGetProperty("raJ2000", out _); }
                    if (!found) await Task.Delay(500);
                }
                Assert.True(found, "Stellarium has no ELink telescope object");
                output.WriteLine($"telescope in Stellarium: ra {info.GetProperty("raJ2000").GetDouble() / 15:0.0000} h, dec {info.GetProperty("decJ2000").GetDouble():0.000}");
                Assert.Equal(5.9195, info.GetProperty("raJ2000").GetDouble() / 15, 2);
                Assert.Equal(7.407, info.GetProperty("decJ2000").GetDouble(), 1);

                // 4. select Vega in Stellarium and use its own "move telescope to selection" (Ctrl+1)
                using var remote = new StellariumRemote($"http://127.0.0.1:{remotePort}");
                await remote.ShowAsync("Vega");
                await Task.Delay(1500);
                var sel = await remote.GetSelectionAsync();
                Assert.NotNull(sel);
                output.WriteLine($"selection read through ELink: {sel!.Name} {sel.RaHours:0.0000} {sel.DecDegrees:0.000}");
                Assert.Equal(18.6156, sel.RaHours, 2);
                int before = pointer.Gotos.Count;
                using var act = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("id", "actionMove_Telescope_To_Selection_1") });
                (await http.PostAsync("api/stelaction/do", act)).EnsureSuccessStatusCode();
                for (int i = 0; i < 40 && pointer.Gotos.Count == before; i++) await Task.Delay(250);
                Assert.True(pointer.Gotos.Count > before, "the slew from Stellarium did not arrive");
                var g = pointer.Gotos[^1];
                output.WriteLine($"goto from Stellarium: {g.RaHours.Value:0.0000} h {g.DecDegrees.Value:0.000}");
                Assert.Equal(18.6156, g.RaHours.Value, 2); Assert.Equal(38.78, g.DecDegrees.Value, 1);
            }
            finally { Stop(stel); }
        }
        finally { try { Directory.Delete(profile, true); } catch { } }
    }
}
