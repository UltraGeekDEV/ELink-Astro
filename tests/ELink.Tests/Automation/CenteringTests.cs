using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

public class CenteringTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FakeMount _mount = null!;
    private FakeSolver _solver = null!;
    private CenteringService _svc = null!;
    private CenteringState? _last;
    private string _file = "";

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("CE-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _mount = new FakeMount(_node, "m"); await _mount.StartAsync();
        _solver = new FakeSolver(_node, _mount); await _solver.StartAsync();
        _file = Path.Combine(Path.GetTempPath(), "elink-center-" + Guid.NewGuid().ToString("N") + ".json");
        _svc = new CenteringService(_node, _file) { SettleDelay = TimeSpan.FromMilliseconds(30) };
        await _svc.StartAsync();
        await _node.HookEventAsync(CenteringIds.State, (CenteringState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync(); await _solver.DisposeAsync(); await _mount.DisposeAsync(); _node.Dispose();
        try { File.Delete(_file); } catch { }
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(15); }
        return cond();
    }

    private Task<CommandResult> Configure(bool enabled = true, bool useOffset = true, bool sync = true) =>
        Commands.CallAsync(_node, CenteringIds.Configure, new CenteringConfig
        {
            Enabled = enabled, MountId = "m", GuideShooterId = "guide", PrimaryShooterId = "main", ExposureSeconds = 1, ToleranceArcmin = 0.5, MaxIterations = 5,
            UseOffset = useOffset, SyncMount = sync,
        });

    private static double Arcmin((double Ra, double Dec) a, (double Ra, double Dec) b) => Sky.SeparationDegrees(a.Ra, a.Dec, b.Ra, b.Dec) * 60;

    [Fact]
    public async Task AHandControllerGotoIsCentredBySyncingAndReslewing()
    {
        _mount.ErrorDeg = (0.5, -0.3);                       // the mount thinks it is 35' from where it really points
        Assert.True((await Configure(useOffset: false)).Ok.Value);
        var target = (Ra: 6.0, Dec: 20.0);
        await _mount.HandControllerGotoAsync(target.Ra, target.Dec);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Centered" or "Failed" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Centered", _last!.Phase.Text);
        Assert.True(Arcmin(_mount.True, target) < 0.5, $"off by {Arcmin(_mount.True, target):0.00}'");
        Assert.True(_mount.Syncs >= 1);
        Assert.Equal(1, _last.Centerings.Value);
        // its own corrective slews do not start another centring, nor does the same target again
        await Task.Delay(400);
        Assert.Equal(1, _last.Centerings.Value);
        int solves = _solver.Solves;
        await _mount.HandControllerGotoAsync(target.Ra, target.Dec);
        await Task.Delay(400);
        Assert.Equal(solves, _solver.Solves);
    }

    [Fact]
    public async Task TheLearnedOffsetPutsThePrimaryNotTheGuideScopeOnTheTarget()
    {
        _solver.PrimaryOffsetDeg = (0.4, 0.2);               // the primary looks 24' east, 12' north of the guide scope
        Assert.True((await Configure()).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, CenteringIds.CalibrateOffset, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { OffsetKnown.Value: true }));
        Assert.Equal(24, _last!.OffsetEastArcmin.Value, 1);
        Assert.Equal(12, _last.OffsetNorthArcmin.Value, 1);

        _mount.ErrorDeg = (-0.3, 0.6);
        var target = (Ra: 10.0, Dec: 40.0);
        await _mount.HandControllerGotoAsync(target.Ra, target.Dec);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Centered" or "Failed" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Centered", _last!.Phase.Text);
        var primary = Gnomonic.ToSky(_mount.True.Ra, _mount.True.Dec, 0.4, 0.2);
        Assert.True(Arcmin(primary, target) < 0.6, $"the primary is {Arcmin(primary, target):0.00}' off");
        Assert.True(Arcmin(_mount.True, target) > 20, "and the guide scope is deliberately not on the target");
    }

    [Fact]
    public async Task AfterAMeridianFlipTheOffsetIsTurnedOver()
    {
        _solver.PrimaryOffsetDeg = (0.4, 0.2);
        Assert.True((await Configure()).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, CenteringIds.CalibrateOffset, NOTESVoid.Void)).Ok.Value);    // learned on the West side
        _mount.PierSide = "East"; await _mount.PublishAsync();
        var target = (Ra: 14.0, Dec: -10.0);
        await _mount.HandControllerGotoAsync(target.Ra, target.Dec);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Centered" or "Failed" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        var primary = Gnomonic.ToSky(_mount.True.Ra, _mount.True.Dec, -0.4, -0.2);   // on the East side the primary looks the other way
        Assert.True(Arcmin(primary, target) < 0.6, $"the primary is {Arcmin(primary, target):0.00}' off");
        Assert.Contains("turned over", _last!.Message.Text + " " + string.Join(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMountWhoseSyncsDoNotHelpIsCentredByAimingOff()
    {
        _mount.SyncDoesNotHelp = true;
        _solver.SimLikeFieldShiftDeg = (0.3, 0.25);           // like the simulator: the field sits off whatever the mount reports
        Assert.True((await Configure(useOffset: false)).Ok.Value);
        var target = (Ra: 2.0, Dec: 30.0);
        await _mount.HandControllerGotoAsync(target.Ra, target.Dec);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Centered" or "Failed" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal("Centered", _last!.Phase.Text);
        var field = Gnomonic.ToSky(_mount.Reported.Ra, _mount.Reported.Dec, 0.3, 0.25);
        Assert.True(Arcmin(field, target) < 0.5);
    }

    [Fact]
    public async Task NothingHappensWhenDisabledAndBadConfigIsRefused()
    {
        Assert.True((await Configure(enabled: false)).Ok.Value);
        await _mount.HandControllerGotoAsync(3, 3);
        await Task.Delay(500);
        Assert.Equal(0, _solver.Solves);
        Assert.False((await Commands.CallAsync(_node, CenteringIds.Configure, new CenteringConfig { MountId = "m" })).Ok.Value);   // no guide camera
        Assert.False((await Commands.CallAsync(_node, CenteringIds.Configure, new CenteringConfig())).Ok.Value);
    }

    [Fact]
    public async Task AFrameThatDoesNotSolveFailsTheCentringWithAReason()
    {
        _solver.Fail = true;
        Assert.True((await Configure()).Ok.Value);
        await _mount.HandControllerGotoAsync(7, 7);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Failed" }));
        Assert.Contains("did not solve", _last!.Message.Text);
    }

    [Fact]
    public async Task CentreNowWorksWithoutASlewAndTheOffsetSurvivesARestart()
    {
        _mount.ErrorDeg = (0.2, 0.2);
        _solver.PrimaryOffsetDeg = (0.1, -0.1);
        Assert.True((await Configure(enabled: false)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, CenteringIds.CalibrateOffset, NOTESVoid.Void)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, CenteringIds.CenterNow, new SkyTarget { RaHours = 8, DecDegrees = 15, Epoch = "J2000" })).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Centered" or "Failed" }));
        Assert.Equal("Centered", _last!.Phase.Text);

        using var node2 = ElinkNode.Create("CE2-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        await using var again = new CenteringService(node2, _file);
        await again.StartAsync();
        var state = (await node2.CallFunctionAsync<NOTESVoid, CenteringState>(CenteringIds.GetState, NOTESVoid.Void))!.Single();
        Assert.True(state.OffsetKnown.Value);
        Assert.Equal(6, state.OffsetEastArcmin.Value, 1);
        Assert.Equal("m", state.Config.MountId.Text);
    }
}
