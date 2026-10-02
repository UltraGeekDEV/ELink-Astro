using System.Collections.Concurrent;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using ELink.IndiBridge;
using ELink.Tests.Indi;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests.Compose;

/// <summary>The scope flips by itself when its target crosses the meridian: between exposures (waiting for the flip
/// point rather than exposing across it) and while idling on target. On INDI's telescope simulator, which picks its
/// pier side from the hour angle at each goto, as real German mounts do.</summary>
public class MeridianFlipTests(ITestOutputHelper log) : IAsyncLifetime
{
    private const double Lat = 47.5, Lon = 19.04, FlipAfter = 0.002;   // flip 7 s past the meridian
    private IndiServerProcess _indi = null!;
    public async Task InitializeAsync()
    {
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), "indi_simulator_telescope");
        Assert.True(await _indi.WaitListeningAsync());
    }
    public Task DisposeAsync() { _indi.Dispose(); return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    /// <summary>J2000 RA of a target at this hour angle (hours) right now.</summary>
    private static double RaAt(double hourAngle, double dec)
    {
        var now = DateTime.UtcNow;
        double raDate = Precession.NormalizeHours(Horizon.LocalSiderealHours(now, Lon) - hourAngle);
        return Precession.DateToJ2000(raDate, dec, now).RaHours;
    }

    private sealed record Rig(TypeSafeEVentNode Node, IndiClient Client, FakeShooter Shooter, IAsyncDisposable[] Owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { foreach (var o in Owned.Reverse()) await o.DisposeAsync(); Node.Dispose(); }
    }

    private async Task<Rig> StartAsync()
    {
        var node = ElinkNode.Create("MF-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port); await link.StartAsync();
        var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        await c.WaitForAsync("Telescope Simulator", "CONNECTION", _ => true, TimeSpan.FromSeconds(30));
        await c.SetSwitchAsync("Telescope Simulator", "CONNECTION", "CONNECT");
        await c.WaitForAsync("Telescope Simulator", "GEOGRAPHIC_COORD", _ => true, TimeSpan.FromSeconds(30));
        var site = new SiteService(node) { Tick = TimeSpan.FromSeconds(1) }; await site.StartAsync();
        Assert.True((await Commands.CallAsync(node, SiteIds.Configure(SiteIds.Default), new SiteConfig { LatitudeDegrees = Lat, LongitudeDegrees = Lon })).Ok.Value);
        await c.WaitForAsync("Telescope Simulator", "GEOGRAPHIC_COORD", p => Math.Abs(p.Number("LAT") - Lat) < 1e-3, TimeSpan.FromSeconds(30));   // the site told the mount
        await c.SetSwitchAsync("Telescope Simulator", "TELESCOPE_PARK", "UNPARK"); await Task.Delay(300);
        var compose = new CompositionHost(node); await compose.StartAsync();
        var shooter = new FakeShooter(node, "cam"); await shooter.StartAsync();
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "eq", MountId = "Telescope_Simulator" })).Ok.Value);
        var scope = new ScopeDefinition { Id = "gem", DisplayName = "gem", MeridianFlip = true, FlipAfterHours = FlipAfter };
        scope.Pointers.Add("eq"); scope.Shooters.Add(new ScopeShooterRef { Id = "cam" });
        Assert.True((await Commands.CallAsync(node, ScopeIds.Define, scope)).Ok.Value);
        return new Rig(node, c, shooter, [link, c, site, compose, shooter]);
    }

    /// <summary>Close to the meridian, on the east side (pier West), so the test does not wait for long slews.</summary>
    private static async Task ApproachAsync(Rig rig, double hourAngle, double dec)
    {
        Assert.True((await Commands.CallAsync(rig.Node, PointerIds.Goto("gem"), new SkyTarget { RaHours = RaAt(hourAngle, dec), DecDegrees = dec, Epoch = "J2000" })).Ok.Value);
        await rig.Client.WaitForAsync("Telescope Simulator", "EQUATORIAL_EOD_COORD", p => p.State == IndiState.Ok, TimeSpan.FromSeconds(180));
        await Task.Delay(1500);
        Assert.True(rig.Client.GetProperty("Telescope Simulator", "TELESCOPE_PIER_SIDE")!.Switch("PIER_WEST"), "east of the meridian the simulator is on pier West");
    }

    [Fact]
    public async Task FlipsBetweenExposuresNeverDuringOne()
    {
        await using var rig = await StartAsync();
        var node = rig.Node;
        await ApproachAsync(rig, -0.03, 30);
        var phases = new ConcurrentQueue<(int Done, string Phase)>();
        ScopeState? state = null;
        await node.HookEventAsync(ScopeIds.State("gem"), (ScopeState s) => { state = s; if (phases.LastOrDefault().Phase != s.Phase.Text) phases.Enqueue((s.ShotsDone.Value, s.Phase.Text)); });

        // 8 s rounds starting 14 s before the meridian: the third would end past the flip point, so it waits and flips first
        var r = await Commands.CallAsync(node, ScopeIds.Command("gem", "Observe"), new ObserveRequest
        {
            Target = new SkyTarget { RaHours = RaAt(-0.004, 30), DecDegrees = 30, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 8 }, Count = 4,
        });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => state is { Observing.Value: false }, 300000), $"{state?.Phase.Text} {state?.Message.Text}");
        log.WriteLine(string.Join(" → ", phases.Select(p => p.Phase)));
        Assert.Equal("Idle", state!.Phase.Text);
        Assert.Equal(4, state.ShotsDone.Value);
        Assert.True(rig.Client.GetProperty("Telescope Simulator", "TELESCOPE_PIER_SIDE")!.Switch("PIER_EAST"), "turned over to pier East");
        var seq = phases.Select(p => p.Phase).ToList();
        int waiting = seq.IndexOf("WaitingForFlip"), flipping = seq.IndexOf("Flipping");
        Assert.True(waiting > 0 && flipping > waiting, string.Join(",", seq));
        // two rounds finished before the flip, the other two after it: no exposure was running while it flipped
        Assert.Equal(2, phases.First(p => p.Phase == "WaitingForFlip").Done);
        Assert.Equal(2, phases.First(p => p.Phase == "Flipping").Done);
        Assert.Equal(4, rig.Shooter.Exposures);
    }

    [Fact]
    public async Task FlipsWhileIdlingOnTargetAcrossTheMeridian()
    {
        await using var rig = await StartAsync();
        await ApproachAsync(rig, -0.001, 40);   // 4 s before the meridian, then nothing to do but track
        PointerState? p = null;
        await rig.Node.HookEventAsync(PointerIds.State("gem"), (PointerState s) => p = s);
        Assert.True(await Eventually(() => rig.Client.GetProperty("Telescope Simulator", "TELESCOPE_PIER_SIDE")!.Switch("PIER_EAST"), 90000),
            "the idle scope flipped by itself once past the flip point");
        Assert.True(await Eventually(() => p is { PierSide.Text: "East", OnTarget.Value: true }, 60000), $"{p?.Phase.Text} {p?.PierSide.Text}");
    }
}
