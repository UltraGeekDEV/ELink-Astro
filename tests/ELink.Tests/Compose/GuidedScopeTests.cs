using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Compose;

/// <summary>A smart scope guides by itself: guiding starts when its pointers settle, stops before they move, and every
/// exposure round waits until settled, dithering every N rounds. Whoever drives the scope never sees any of it.</summary>
public class GuidedScopeTests : IAsyncLifetime
{
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;
    private FakeShooter _shooter = null!;
    private FakeGuider _guider = null!;
    private ScopeState? _state;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("GS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _pointer = new FakePointer(_node, "pm", 80); await _pointer.StartAsync();
        _shooter = new FakeShooter(_node, "cam"); await _shooter.StartAsync();
        _guider = new FakeGuider(_node, "g1"); await _guider.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _guider.DisposeAsync(); await _shooter.DisposeAsync(); await _pointer.DisposeAsync();
        _node.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private async Task<SmartScope> Scope(string id, int ditherEvery, string guider = "g1", params string[] shooters)
    {
        var def = new ScopeDefinition { Id = id, DisplayName = id, GuiderId = guider, DitherEvery = ditherEvery, DitherPixels = 4, SettleTimeoutSeconds = 10 };
        def.Pointers.Add("pm");
        foreach (var s in shooters.Length == 0 ? ["cam"] : shooters) def.Shooters.Add(new ScopeShooterRef { Id = s });
        var scope = new SmartScope(_node, def);
        await scope.StartAsync();
        await _node.HookEventAsync(ScopeIds.State(id), (ScopeState s) => _state = s);
        return scope;
    }

    private static ObserveRequest Observe(int count) => new()
    {
        Target = new SkyTarget { RaHours = 5, DecDegrees = 10, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 0.05 }, Count = count,
    };

    [Fact]
    public async Task GuidesAfterTheSlewAndDithersEveryNRounds()
    {
        await using var scope = await Scope("gs", ditherEvery: 2);
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("gs", "Observe"), Observe(5))).Ok.Value);
        Assert.True(await Eventually(() => _state is { Observing.Value: false, ShotsDone.Value: 5 }), $"{_state?.Phase.Text} {_state?.Message.Text}");
        Assert.Equal("", _state!.Message.Text);
        var calls = _guider.Snapshot();
        // stopped before the slew, started once on target, then settle / dither per round
        Assert.Equal("Stop", calls[0]);
        Assert.Contains("Start", calls);
        Assert.True(calls.IndexOf("Start") < calls.IndexOf("Settle"));
        Assert.Equal(new[] { "Settle", "Settle", "Dither 4", "Settle", "Dither 4" }, calls.Where(c => c is "Settle" || c.StartsWith("Dither")).ToArray());
        Assert.Equal(5, _shooter.Exposures);

        // the next target: guiding stops before the mount moves and starts again once it is there
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("gs", "Observe"), Observe(1))).Ok.Value);
        Assert.True(await Eventually(() => _state is { Observing.Value: false, ShotsDone.Value: 1 }));
        calls = _guider.Snapshot();
        int lastStop = calls.LastIndexOf("Stop"), lastStart = calls.LastIndexOf("Start");
        Assert.True(lastStop < lastStart);
    }

    [Fact]
    public async Task AParentScopeDrivingThisOneStillGetsGuidedFrames()
    {
        await using var child = await Scope("child", ditherEvery: 1);
        var parentDef = new ScopeDefinition { Id = "parent", DisplayName = "parent" };
        parentDef.Pointers.Add("child"); parentDef.Shooters.Add(new ScopeShooterRef { Id = "child" });
        await using var parent = new SmartScope(_node, parentDef);
        await parent.StartAsync();
        ScopeState? ps = null;
        await _node.HookEventAsync(ScopeIds.State("parent"), (ScopeState s) => ps = s);
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("parent", "Observe"), Observe(3))).Ok.Value);
        Assert.True(await Eventually(() => ps is { Observing.Value: false, ShotsDone.Value: 3 }), $"{ps?.Phase.Text} {ps?.Message.Text}");
        var calls = _guider.Snapshot();
        Assert.Contains("Start", calls);
        // the child dithered by itself between the parent's rounds
        Assert.Equal(new[] { "Settle", "Dither 4", "Dither 4" }, calls.Where(c => c is "Settle" || c.StartsWith("Dither")).ToArray());
    }

    [Fact]
    public async Task AGuiderThatCannotStartFailsTheRunAndOneThatIsSlowToSettleIsNoted()
    {
        await using var scope = await Scope("gs2", ditherEvery: 0);
        _guider.FailStart = true;
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("gs2", "Observe"), Observe(2))).Ok.Value);
        Assert.True(await Eventually(() => _state is { Observing.Value: false, Phase.Text: "Error" }), $"{_state?.Phase.Text} {_state?.Message.Text}");
        Assert.True(_state!.Message.Text.Contains("guiding"), _state.Message.Text);
        Assert.Equal(0, _shooter.Exposures);

        _guider.FailStart = false; _guider.NeverSettles = true;
        Assert.True((await Commands.CallAsync(_node, ScopeIds.Command("gs2", "Observe"), Observe(2))).Ok.Value);
        Assert.True(await Eventually(() => _state is { Observing.Value: false, ShotsDone.Value: 2 }), $"{_state?.Phase.Text} {_state?.Message.Text}");
        Assert.Contains("did not settle", _state!.Message.Text);
        Assert.Equal(2, _shooter.Exposures);
    }
}
