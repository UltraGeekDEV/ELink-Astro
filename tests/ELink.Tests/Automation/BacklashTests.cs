using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Tests.Compose;
using Event.Connections.Models.BaseBinaryConvertibles;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>A mount with slack in its gears, a solver that sees where it really points: the slack is measured, kept, and reused.</summary>
public class BacklashTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private CommandSet _fakes = null!;
    private FakePointer _mount = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elink-backlash-" + Guid.NewGuid().ToString("N"));
    private const double RaSlack = 40, DecSlack = 90;     // arcseconds of each axis

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("BL-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _fakes = new CommandSet(_node);
        var snap = new CompositionSnapshot();
        var def = new ScopeDefinition { Id = "s", DisplayName = "s" }; def.Pointers.Add("m");
        snap.Scopes.Add(def);
        await _fakes.AddAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, _ => Task.FromResult(snap), "fake composition");
        _mount = new FakePointer(_node, "s", 10); await _mount.StartAsync();
        // the solver: where the scope really is after the moves so far (it stops short by half the slack, on the side it came from)
        await _fakes.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, _ =>
        {
            var (ra, dec) = Truth(_mount.Gotos);
            return Task.FromResult(new SolveResult { Solved = true, RaHours = ra, DecDegrees = dec, PixelScale = 2 });
        }, "fake solver");
        Assert.True((await Commands.CallAsync(_node, PointerIds.Goto("s"), new SkyTarget { RaHours = 5.6, DecDegrees = 20, Epoch = "J2000" })).Ok.Value);
        await Task.Delay(100);
    }

    public static (double Ra, double Dec) Truth(IReadOnlyList<SkyTarget> gotos, double raSlack = RaSlack, double decSlack = DecSlack)
    {
        int dRa = 0, dDec = 0;
        for (int i = 1; i < gotos.Count; i++)
        {
            double a = gotos[i].RaHours.Value - gotos[i - 1].RaHours.Value, b = gotos[i].DecDegrees.Value - gotos[i - 1].DecDegrees.Value;
            if (Math.Abs(a) > 1e-9) dRa = Math.Sign(a);
            if (Math.Abs(b) > 1e-9) dDec = Math.Sign(b);
        }
        var last = gotos[^1];
        return (last.RaHours.Value - dRa * raSlack / 2 / 3600 / 15, last.DecDegrees.Value - dDec * decSlack / 2 / 3600);
    }

    public async Task DisposeAsync()
    {
        await _mount.DisposeAsync(); _fakes.Dispose(); _node.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task TheSlackOfEachAxisIsMeasuredKeptAndTheScopeEndsWhereItStarted()
    {
        var svc = new BacklashService(_node, _dir) { Settle = TimeSpan.Zero }; await svc.StartAsync();
        var m = (await _node.CallFunctionAsync<BinaryConvertibleString, BacklashMeasurement>(BacklashIds.Measure, (BinaryConvertibleString)"s", TimeSpan.FromSeconds(60)))!.Single();
        Assert.True(m.Ok.Value, m.Message.Text);
        Assert.Equal("m", m.MountId.Text);
        Assert.Equal(RaSlack, m.RaArcsec.Value, 1);
        Assert.Equal(DecSlack, m.DecArcsec.Value, 1);
        Assert.Equal(5.6, _mount.Gotos[^1].RaHours.Value, 9); Assert.Equal(20, _mount.Gotos[^1].DecDegrees.Value, 9);
        await svc.DisposeAsync();

        // kept: another start of the station knows it
        var again = new BacklashService(_node, _dir); await again.StartAsync();
        var st = (await _node.CallFunctionAsync<NOTESVoid, BacklashState>(BacklashIds.GetState, NOTESVoid.Void))!.Single();
        var kept = Assert.Single(st.Mounts);
        Assert.Equal(DecSlack, kept.DecArcsec.Value, 1);
        await again.DisposeAsync();
    }
}
