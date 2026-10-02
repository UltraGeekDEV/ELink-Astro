using System.Diagnostics;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.IndiBridge;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Indi;

/// <summary>Equipment profiles: ELink runs the drivers itself (its own indiserver), connects the devices, and survives
/// the server dying.</summary>
public class ProfileTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private DeviceDirectory _dir = null!;
    private ProfileService _svc = null!;
    private ProfileState? _last;
    private readonly string _file = Path.Combine(Path.GetTempPath(), "elink-profiles-" + Guid.NewGuid().ToString("N") + ".bin");
    private readonly int _port = ELink.Testing.TestPorts.Next();

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("PR-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _dir = new DeviceDirectory(_node); await _dir.StartAsync();
        _svc = new ProfileService(_node, _dir, _file); await _svc.StartAsync();
        await _node.HookEventAsync(ProfileIds.State, (ProfileState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync(); _node.Dispose();
        try { File.Delete(_file); } catch { }
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    private async Task<List<DeviceInfo>> DevicesAsync() =>
        ((await _node.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void)) ?? new()).SelectMany(l => l.Devices).ToList();

    private int ServersOnMyPort()
    {
        using var p = Process.Start(new ProcessStartInfo("pgrep", $"-f \"[i]ndiserver -p {_port} \"") { RedirectStandardOutput = true, UseShellExecute = false })!;
        string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [Fact]
    public void TheCatalogueListsTheInstalledDrivers()
    {
        var cat = _svc.Catalog();
        Assert.Contains(cat.Drivers, d => d.Executable.Text == "indi_simulator_telescope" && d.Group.Text == "Telescopes");
        Assert.Contains(cat.Drivers, d => d.Executable.Text == "indi_simulator_ccd");
        Assert.True(cat.Drivers.Count > 100);
    }

    [Fact]
    public async Task RunsAProfileConnectsItsDevicesAndSurvivesTheServerDying()
    {
        Assert.False((await Commands.CallAsync(_node, ProfileIds.Save, new EquipmentProfile { Label = "empty" })).Ok.Value);
        var rig = new EquipmentProfile { Label = "Sim rig", Port = _port };
        rig.Drivers.Add("indi_simulator_telescope"); rig.Drivers.Add("indi_simulator_ccd");
        Assert.True((await Commands.CallAsync(_node, ProfileIds.Save, rig)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, ProfileIds.Start, (BinaryConvertibleString)"Sim rig", TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Running", Running.Text: "Sim rig" }), $"{_last?.Phase.Text} {_last?.Message.Text}");

        // the devices are on the mesh, and connected without anyone asking
        Assert.True(await Eventually(() => DevicesAsync().GetAwaiter().GetResult().Any(d => d.Kind.Text == "Mount" && d.Id.Text == "Telescope_Simulator")));
        using var mount = new RemoteState<MountState>(_node, EquipmentIds.State("Mount", "Telescope_Simulator"), EquipmentIds.GetState("Mount", "Telescope_Simulator"));
        await mount.StartAsync();
        Assert.True(await Eventually(() => mount.Latest is { Connected.Value: true }), "the mount was connected by the profile");
        Assert.Equal(1, ServersOnMyPort());

        // the server dies: it is started again, and the devices come back connected
        Process.Start("pkill", $"-f \"[i]ndiserver -p {_port} \"")!.WaitForExit();
        Assert.True(await Eventually(() => _last is { Restarts.Value: 1, Phase.Text: "Running" }, 60000), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.True(await Eventually(() => mount.Latest is { Connected.Value: true }, 60000), "connected again after the restart");

        // kept, and stopped cleanly
        await using (var again = new ProfileService(_node, _dir, _file))
        {
            var answers = await _node.CallFunctionAsync<NOTESVoid, EquipmentProfiles>(ProfileIds.List, NOTESVoid.Void);
            Assert.Contains(answers!.SelectMany(a => a.Profiles), p => p.Label.Text == "Sim rig");
        }
        Assert.True((await Commands.CallAsync(_node, ProfileIds.Stop, NOTESVoid.Void, TimeSpan.FromSeconds(60))).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Stopped" }));
        Assert.True(await Eventually(() => ServersOnMyPort() == 0), "its indiserver is gone");
    }
}
