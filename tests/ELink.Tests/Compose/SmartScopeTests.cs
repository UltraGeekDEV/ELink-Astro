using System.Net;
using System.Net.Sockets;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Compose;

public class SmartScopeTests : IAsyncLifetime
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private TypeSafeEVentNode _node = null!;

    public Task InitializeAsync() { _node = ElinkNode.Create("SS-" + Guid.NewGuid().ToString("N")[..6], FreePort()); return Task.CompletedTask; }
    public Task DisposeAsync() { _node.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static ScopeDefinition Scope(string id, string[] pointers, params (string id, double e, double n)[] shooters)
    {
        var d = new ScopeDefinition { Id = id, DisplayName = id };
        foreach (var p in pointers) d.Pointers.Add(p);
        foreach (var s in shooters) d.Shooters.Add(new ScopeShooterRef { Id = s.id, OffsetEastArcmin = s.e, OffsetNorthArcmin = s.n });
        return d;
    }

    private async Task<(List<ShotEvent> shots, Func<ScopeState?> state)> Watch(string scopeId)
    {
        var shots = new List<ShotEvent>();
        await _node.HookEventAsync(ShooterIds.Shot(scopeId), (ShotEvent s) => { lock (shots) shots.Add(s); });
        ScopeState? last = null;
        await _node.HookEventAsync(ScopeIds.State(scopeId), (ScopeState s) => last = s);
        return (shots, () => last);
    }

    [Fact]
    public async Task OneMountTwoCamerasObserve()
    {
        await using var p = new FakePointer(_node, "mount1"); await p.StartAsync();
        await using var a = new FakeShooter(_node, "camA"); await a.StartAsync();
        await using var b = new FakeShooter(_node, "camB"); await b.StartAsync();
        await using var scope = new SmartScope(_node, Scope("duo", new[] { "mount1" }, ("camA", 0, 0), ("camB", 30, 0)));
        await scope.StartAsync();
        var (shots, state) = await Watch("duo");

        var target = new SkyTarget { RaHours = 5.5, DecDegrees = 20, Epoch = "J2000" };
        var r = await Commands.CallAsync(_node, ScopeIds.Command("duo", "Observe"),
            new ObserveRequest { Target = target, Exposure = new ShooterExposure { Seconds = 0.2, Filter = "" }, Count = 2 });
        Assert.True(r.Ok.Value, r.Error.Text);

        Assert.True(await Eventually(() => state() is { Observing.Value: false, ShotsDone.Value: 2 }), "observe did not finish");
        Assert.Equal("Idle", state()!.Phase.Text);   // a finished run goes back to Idle
        Assert.Single(p.Gotos);
        Assert.Equal(2, a.Exposures); Assert.Equal(2, b.Exposures);
        Assert.True(await Eventually(() => { lock (shots) return shots.Count == 4; }));
        lock (shots) { Assert.All(shots, s => { Assert.Equal(5.5, s.PointingRaHours.Value, 6); Assert.Equal(20, s.PointingDecDegrees.Value, 6); }); }
    }

    [Fact]
    public async Task ScopesComposeIntoScopes()
    {
        // inner: one pointer, two shooters.  outer: two pointers (one is the inner scope itself), two shooters (one is the inner scope).
        await using var p1 = new FakePointer(_node, "pInner"); await p1.StartAsync();
        await using var p2 = new FakePointer(_node, "pOuter"); await p2.StartAsync();
        await using var x = new FakeShooter(_node, "x"); await x.StartAsync();
        await using var y = new FakeShooter(_node, "y"); await y.StartAsync();
        await using var z = new FakeShooter(_node, "z"); await z.StartAsync();
        await using var inner = new SmartScope(_node, Scope("inner", new[] { "pInner" }, ("x", 0, 0), ("y", 0, 0)));
        await inner.StartAsync();
        await using var outer = new SmartScope(_node, Scope("outer", new[] { "pOuter", "inner" }, ("z", 0, 0), ("inner", 0, 0)));
        await outer.StartAsync();
        var (shots, state) = await Watch("outer");

        var r = await Commands.CallAsync(_node, ScopeIds.Command("outer", "Observe"),
            new ObserveRequest { Target = new SkyTarget { RaHours = 1, DecDegrees = 2, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 0.1 }, Count = 2 });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => state() is { Observing.Value: false, ShotsDone.Value: 2 }), "observe did not finish: " + state()?.Phase.Text + state()?.Message.Text);

        Assert.Single(p1.Gotos); Assert.Single(p2.Gotos);          // both pointers, one of them through the inner scope
        Assert.Equal(2, x.Exposures); Assert.Equal(2, y.Exposures); Assert.Equal(2, z.Exposures);
        Assert.True(await Eventually(() => { lock (shots) return shots.Count == 6; }), "expected 3 shots per round x 2");

        // the outer scope presents itself as a Pointer and a Shooter as well
        var ps = (await _node.CallFunctionAsync<NOTESVoid, PointerState>(PointerIds.GetState("outer"), NOTESVoid.Void))!.Single();
        Assert.Equal("OnTarget", ps.Phase.Text);
        var ss = (await _node.CallFunctionAsync<NOTESVoid, ShooterState>(ShooterIds.GetState("outer"), NOTESVoid.Void))!.Single();
        Assert.Equal(3, ss.ShotsPerExposure.Value);
    }

    [Fact]
    public async Task ObservedFramesCarryTheRunsObjectAndPlanButManualOnesDoNot()
    {
        await using var p = new FakePointer(_node, "tm", 50); await p.StartAsync();
        await using var a = new FakeShooter(_node, "ta"); await a.StartAsync();
        await using var scope = new SmartScope(_node, Scope("tagged", new[] { "tm" }, ("ta", 0, 0)));
        await scope.StartAsync();
        var (shots, state) = await Watch("tagged");

        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("tagged", "Observe"), new ObserveRequest
        { Exposure = new ShooterExposure { Seconds = 0.1 }, Count = 2, ObjectName = "M 31", PlanId = "night7" })).Ok.Value);
        Assert.True(await Eventually(() => state() is { Observing.Value: false, ShotsDone.Value: 2 }));
        Assert.True(await Eventually(() => { lock (shots) return shots.Count == 2; }));
        lock (shots) Assert.All(shots, s => { Assert.Equal("M 31", s.ObjectName.Text); Assert.Equal("night7", s.PlanId.Text); });

        Assert.True((await Commands.CallAsync(_node, ShooterIds.Expose("tagged"), new ShooterExposure { Seconds = 0.1 })).Ok.Value);
        Assert.True(await Eventually(() => { lock (shots) return shots.Count == 3; }));
        lock (shots) { Assert.Equal("", shots[2].ObjectName.Text); Assert.Equal("", shots[2].PlanId.Text); }
    }

    [Fact]
    public async Task ShooterOffsetMovesThePointingAxis()
    {
        await using var p = new FakePointer(_node, "m"); await p.StartAsync();
        await using var a = new FakeShooter(_node, "a"); await a.StartAsync();
        await using var scope = new SmartScope(_node, Scope("off", new[] { "m" }, ("a", 60, 30)));   // shooter 1 deg east? no: 60' east, 30' north of the axis
        await scope.StartAsync();
        var r = await Commands.CallAsync(_node, PointerIds.Goto("off"), new SkyTarget { RaHours = 12, DecDegrees = 0, Epoch = "J2000" });
        Assert.True(r.Ok.Value);
        var sent = p.Gotos.Single();
        Assert.Equal(-0.5, sent.DecDegrees.Value, 6);             // axis is 30' south of the target
        Assert.Equal(12 - 60.0 / 60 / 15, sent.RaHours.Value, 6); // and 60' west of it (at dec 0)
    }

    [Fact]
    public async Task ObserveRejectsBadRequestsAndAbortStops()
    {
        await using var p = new FakePointer(_node, "m2", 100); await p.StartAsync();
        await using var a = new FakeShooter(_node, "a2"); await a.StartAsync();
        await using var empty = new SmartScope(_node, Scope("empty", Array.Empty<string>()));
        await empty.StartAsync();
        Assert.False((await Commands.CallAsync(_node, ScopeIds.Command("empty", "Observe"), new ObserveRequest())).Ok.Value);

        await using var scope = new SmartScope(_node, Scope("s2", new[] { "m2" }, ("a2", 0, 0)));
        await scope.StartAsync();
        var (_, state) = await Watch("s2");
        Assert.False((await Commands.CallAsync(_node, ScopeIds.Command("s2", "Observe"), new ObserveRequest { Count = 0 })).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("s2", "Observe"),
            new ObserveRequest { Exposure = new ShooterExposure { Seconds = 5 }, Count = 100 })).Ok.Value);
        Assert.False((await Commands.CallAsync(_node, ScopeIds.Command("s2", "Observe"), new ObserveRequest())).Ok.Value);   // already observing
        Assert.True(await Eventually(() => state()?.Phase.Text == "Exposing"));
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("s2", "Abort"), NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => state() is { Observing.Value: false }, 10000));
        Assert.Equal("aborted", state()!.Message.Text);
    }

    [Fact]
    public async Task ObserveFailsFastWhenAPointerCannotSettle()
    {
        await using var p = new FakePointer(_node, "pk", 50) { StuckPhase = "Parked" }; await p.StartAsync();
        await using var a = new FakeShooter(_node, "ak"); await a.StartAsync();
        await using var scope = new SmartScope(_node, Scope("pks", new[] { "pk" }, ("ak", 0, 0)));
        await scope.StartAsync();
        var (_, state) = await Watch("pks");
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("pks", "Observe"),
            new ObserveRequest { Exposure = new ShooterExposure { Seconds = 0.1 }, SlewTimeoutSeconds = 120 })).Ok.Value);
        Assert.True(await Eventually(() => state() is { Observing.Value: false }, 5000), "should not wait for the 120 s timeout");
        Assert.Equal("Error", state()!.Phase.Text);
        Assert.Contains("Parked", state()!.Message.Text);
        Assert.Equal(0, a.Exposures);
    }

    [Fact]
    public async Task HostDefinesPersistsAndRestores()
    {
        string file = Path.Combine(Path.GetTempPath(), "elink-compose-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using (var host = new CompositionHost(_node, file))
            {
                await host.StartAsync();
                await using var p = new FakePointer(_node, "mm"); await p.StartAsync();
                Assert.True((await Commands.CallAsync(_node, ScopeIds.Define, Scope("persisted", new[] { "mm" }, ("cam", 1, 2)))).Ok.Value);
                Assert.False((await Commands.CallAsync(_node, ScopeIds.Define, Scope("bad id!", Array.Empty<string>()))).Ok.Value);
                Assert.False((await Commands.CallAsync(_node, ScopeIds.Define, Scope("self", new[] { "self" }))).Ok.Value);
                var snap = (await _node.CallFunctionAsync<NOTESVoid, CompositionSnapshot>(ScopeIds.Snapshot, NOTESVoid.Void))!.Single();
                Assert.Equal("persisted", snap.Scopes.Single().Id.Text);
            }
            using var node2 = ElinkNode.Create("SS-restore", FreePort());
            await using var host2 = new CompositionHost(node2, file);
            await host2.StartAsync();
            var restored = host2.Snapshot().Scopes.Single();
            Assert.Equal("persisted", restored.Id.Text);
            Assert.Equal("mm", restored.Pointers.Single().Text);
            Assert.Equal(2, restored.Shooters.Single().OffsetNorthArcmin.Value);
        }
        finally { File.Delete(file); }
    }
}
