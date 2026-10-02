using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>An image service that is only endpoints: records what it was asked to take, and is told when an image is done.</summary>
public sealed class FakeImaging : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<ImagingState> _pub;
    private string _phase = "Idle", _label = "";
    public List<string> Started { get; } = new();
    public int Aborts;
    public Dictionary<string, (ImagingRequest Request, double MinSeconds)> Kept { get; } = new();

    public FakeImaging(TypeSafeEVentNode node)
    {
        _cmds = new CommandSet(node);
        _pub = new(node, ImagingIds.State, ImagingIds.GetState, () => new ImagingState { Phase = _phase, Label = _label });
    }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<ImagingRequest, CommandResult>(ImagingIds.Start, async r =>
        {
            lock (Started) Started.Add(r.Label.Text);
            _label = r.Label.Text; _phase = "Running";
            if (!Kept.ContainsKey(r.Label.Text)) Kept[r.Label.Text] = (r, 0);
            await _pub.PublishAsync();
            return CommandResult.Success();
        }, "fake image start");
        await _cmds.AddAsync<NOTESVoid, CommandResult>(ImagingIds.Abort, async _ => { Interlocked.Increment(ref Aborts); _phase = "Aborted"; await _pub.PublishAsync(); return CommandResult.Success(); }, "fake abort");
        await _cmds.AddAsync<NOTESVoid, SavedImages>(ImagingIds.ListSaved, _ =>
        {
            var s = new SavedImages();
            foreach (var (label, k) in Kept) s.Images.Add(new SavedImage { Label = label, Request = k.Request, MinSeconds = k.MinSeconds });
            return Task.FromResult(s);
        }, "fake kept images");
        await _pub.StartAsync();
    }

    /// <summary>The running image reaches its depth.</summary>
    public async Task FinishAsync()
    {
        var k = Kept[_label];
        Kept[_label] = (k.Request, k.Request.TargetSeconds.Value);
        _phase = "Done"; await _pub.PublishAsync();
    }

    public string Phase => _phase;
    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

public class SchedulerTests : IAsyncLifetime
{
    private DateTime _now = new(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);   // a January evening
    private TypeSafeEVentNode _node = null!;
    private SiteService _site = null!;
    private FakeImaging _imaging = null!;
    private SchedulerService _scheduler = null!;
    private SchedulerState? _last;
    private readonly string _file = Path.Combine(Path.GetTempPath(), "elink-schedule-" + Guid.NewGuid().ToString("N") + ".bin");

    public async Task InitializeAsync()
    {
        _node = ElinkNode.Create("SCH-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        _site = new SiteService(_node) { UtcNow = () => _now, Tick = TimeSpan.FromMilliseconds(200) };
        await _site.StartAsync();
        Assert.True((await Commands.CallAsync(_node, SiteIds.Configure(SiteIds.Default), new SiteConfig { LatitudeDegrees = 47.5, LongitudeDegrees = 19.04, MinAltitudeDegrees = 15 })).Ok.Value);
        _imaging = new FakeImaging(_node); await _imaging.StartAsync();
        _scheduler = new SchedulerService(_node, _file) { CheckEvery = TimeSpan.FromMilliseconds(300) };
        await _scheduler.StartAsync();
        await _node.HookEventAsync(SchedulerIds.State, (SchedulerState s) => _last = s);
    }

    public async Task DisposeAsync()
    {
        await _scheduler.DisposeAsync(); await _imaging.DisposeAsync(); await _site.DisposeAsync();
        _node.Dispose();
        try { File.Delete(_file); } catch { }
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private static ScheduleEntry Entry(string label, double raH, double dec, int priority = 0)
    {
        var r = new ImagingRequest { Label = label, Center = new SkyTarget { RaHours = raH, DecDegrees = dec, Epoch = "J2000" }, TargetSeconds = 3600, Exposure = new ShooterExposure { Seconds = 60 } };
        r.ScopeIds.Add("rig");
        return new ScheduleEntry { Request = r, Priority = priority };
    }

    [Fact]
    public async Task RunsTheBestPossibleImageAndMovesOn()
    {
        var s = new Schedule { MinRunMinutes = 30 };
        s.Entries.Add(Entry("M42", 5.588, -5.39));                 // up in the south on a January evening
        s.Entries.Add(Entry("M8", 18.06, -24.4));                  // a summer target: below the horizon
        s.Entries.Add(Entry("NCP", 2.5, 85, priority: 5));         // always up, and wanted first
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);

        Assert.True(await Eventually(() => _last is { Phase.Text: "Imaging", Running.Text: "NCP" }), $"{_last?.Phase.Text} {_last?.Running.Text} {_last?.Message.Text}");
        var m8 = _last!.Entries.First(e => e.Label.Text == "M8");
        Assert.Equal("Waiting", m8.Status.Text);
        Assert.Contains("too low", m8.Reason.Text);
        Assert.Equal("Possible", _last.Entries.First(e => e.Label.Text == "M42").Status.Text);

        await _imaging.FinishAsync();                              // NCP reaches its depth
        Assert.True(await Eventually(() => _last is { Running.Text: "M42" }), $"{_last?.Running.Text} {_last?.Message.Text}");
        Assert.Equal("Complete", _last!.Entries.First(e => e.Label.Text == "NCP").Status.Text);
        Assert.Equal(new[] { "NCP", "M42" }, _imaging.Started);

        // dawn: M42 is stopped (and kept), nothing else is possible
        _now = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Waiting", Running.Text: "" }), $"{_last?.Phase.Text} {_last?.Running.Text} {_last?.Message.Text}");
        Assert.Equal(1, _imaging.Aborts);
        Assert.Contains("not dark", _last!.Entries.First(e => e.Label.Text == "M42").Reason.Text);

        // the next evening it carries on with M42
        _now = new DateTime(2026, 1, 16, 20, 0, 0, DateTimeKind.Utc);
        Assert.True(await Eventually(() => _last is { Running.Text: "M42" }), _last?.Message.Text);
        Assert.Equal(new[] { "NCP", "M42", "M42" }, _imaging.Started);

        // the schedule is kept
        await using var again = new SchedulerService(_node, _file);
        Assert.Equal(3, Assert.Single((await _node.CallFunctionAsync<NOTESVoid, Schedule>(SchedulerIds.GetSchedule, NOTESVoid.Void))!).Entries.Count);
    }

    [Fact]
    public async Task SchedulesNeedNamesAndScopes()
    {
        var s = new Schedule();
        s.Entries.Add(Entry("A", 5, 0)); s.Entries.Add(Entry("a", 6, 0));
        Assert.False((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);    // same name
        var noScope = new Schedule(); var e = Entry("B", 5, 0); e.Request.ScopeIds.Clear(); noScope.Entries.Add(e);
        Assert.False((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, noScope)).Ok.Value);
    }
}
