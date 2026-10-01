using ELink.Core;
using ELink.IndiBridge;
using Xunit;

namespace ELink.Tests.Indi;

public class ReconnectTests
{
    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task LinkSurvivesIndiServerRestart()
    {
        int port = IndiServerProcess.FreePort();
        using var node = ElinkNode.Create("RC-Bridge", IndiServerProcess.FreePort());
        var dir = new DeviceDirectory(node);
        await dir.StartAsync();
        await using var link = new IndiServerLink(node, dir, "rc", "127.0.0.1", port);
        await link.StartAsync();                       // the server is not even running yet: must keep trying

        for (int round = 0; round < 2; round++)
        {
            using (var indi = new IndiServerProcess(port, "indi_simulator_telescope"))
            {
                Assert.True(await indi.WaitListeningAsync());
                Assert.True(await Eventually(() => link.IsConnected && dir.Devices.Any(d => d.Kind.Text == "Mount")), $"round {round}: devices not announced");
            }                                          // server killed here
            Assert.True(await Eventually(() => !link.IsConnected && dir.Devices.Count == 0), $"round {round}: devices not withdrawn");
        }
    }
}
