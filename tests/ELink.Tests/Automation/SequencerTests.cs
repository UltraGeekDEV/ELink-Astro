using System.Net;
using System.Net.Sockets;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

public class SequencerTests : IAsyncLifetime
{
    private static int FreePort() => ELink.Testing.TestPorts.Next();
    private TypeSafeEVentNode _node = null!;
    private FakePointer _pointer = null!;
    private FakeShooter _shooter = null!;
    private SmartScope _scope = null!;
    private SequencerService _seq = null!;
    private SequencerState? _last;

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("SQ-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        _pointer = new FakePointer(_node, "pm", 80); await _pointer.StartAsync();
        _shooter = new FakeShooter(_node, "sc"); await _shooter.StartAsync();
        var def = new ScopeDefinition { Id = "sq", DisplayName = "sq" };
        def.Pointers.Add("pm"); def.Shooters.Add(new ScopeShooterRef { Id = "sc" });
        _scope = new SmartScope(_node, def); await _scope.StartAsync();
        _seq = new SequencerService(_node); await _seq.StartAsync();
        await _node.HookEventAsync(SequencerIds.State, (SequencerState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _seq.DisposeAsync(); await _scope.DisposeAsync(); await _shooter.DisposeAsync(); await _pointer.DisposeAsync();
        _node.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static SequenceBlock Block(string label, double ra, int count, double seconds = 0.1, bool refocus = false) => new()
    {
        Label = label, Count = count, RefocusBefore = refocus,
        Target = new SkyTarget { RaHours = ra, DecDegrees = 10, Epoch = "J2000" },
        Exposure = new ShooterExposure { Seconds = seconds },
    };

    private static SequencePlan Plan(string weather = "", params SequenceBlock[] blocks)
    {
        var p = new SequencePlan { Id = "p1", ScopeId = "sq", WeatherId = weather };
        foreach (var b in blocks) p.Blocks.Add(b);
        return p;
    }

    [Fact]
    public async Task RunsBlocksInOrder()
    {
        var r = await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("A", 1, 2), Block("B", 2, 1)));
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal(3, _last!.ShotsTotal.Value);
        Assert.Equal(2, _last.BlockIndex.Value);
        Assert.Equal(new[] { 1.0, 2.0 }, _pointer.Gotos.Select(g => g.RaHours.Value).ToArray());
        Assert.Equal(3, _shooter.Exposures);
    }

    [Fact]
    public async Task ValidatesPlansAndRunsOneAtATime()
    {
        Assert.False((await Commands.CallAsync(_node, SequencerIds.Start, new SequencePlan { ScopeId = "sq" })).Ok.Value);        // no blocks
        Assert.False((await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("A", 1, 0)))).Ok.Value);                // count 0
        Assert.False((await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("A", 1, 1, refocus: true)))).Ok.Value); // refocus without ids
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("A", 1, 50, 0.2)))).Ok.Value);
        Assert.False((await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("B", 1, 1)))).Ok.Value);                // already running
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Abort, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Aborted" }));
    }

    [Fact]
    public async Task UnsafeWeatherStopsAndLaterResumesWithOnlyTheMissingFrames()
    {
        await using var weather = new FakeWeather(_node, "wx"); await weather.StartAsync();
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, Plan("wx", Block("A", 1, 6, 0.4)))).Ok.Value);
        Assert.True(await Eventually(() => _last is { ShotsInBlock.Value: >= 1 }), "first frame");
        await weather.SetAsync("Unsafe");
        Assert.True(await Eventually(() => _last is { Phase.Text: "WaitingForWeather" }), $"{_last?.Phase.Text}");
        int heldAt = _last!.ShotsInBlock.Value;
        await Task.Delay(1200);                                                  // nothing happens while it is unsafe
        Assert.Equal("WaitingForWeather", _last.Phase.Text);
        Assert.Equal(heldAt, _last.ShotsInBlock.Value);

        await weather.SetAsync("Safe");
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }, 30000), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal(6, _last!.ShotsTotal.Value);                                // exactly the planned frames, none lost or doubled
    }

    [Fact]
    public async Task PauseAndResume()
    {
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, Plan("", Block("A", 1, 5, 0.3)))).Ok.Value);
        Assert.True(await Eventually(() => _last is { ShotsInBlock.Value: >= 1 }));
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Pause, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Paused" }, 8000), $"{_last?.Phase.Text} | {_last?.Message.Text} | shots {_last?.ShotsInBlock.Value}");
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Resume, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }, 30000), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal(5, _last!.ShotsTotal.Value);
    }

    [Fact]
    public async Task RefocusRunsBeforeTheBlockAndAFailureStopsThePlan()
    {
        await using var af = new FakeAutofocus(_node); await af.StartAsync();
        var plan = Plan("", Block("A", 1, 1, refocus: true), Block("B", 2, 1, refocus: true));
        plan.Autofocus = new AutofocusRequest { ShooterId = "sq", FocuserId = "foc" };
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, plan)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Done" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal(2, af.Runs);

        af.Fail = true;
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, plan)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Error" }));
        Assert.Contains("autofocus failed", _last!.Message.Text);
    }
}
