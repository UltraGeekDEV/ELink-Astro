using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>An image service that is only endpoints: records what it was asked to take, and is told when an image is done.</summary>
public sealed class FakeImaging : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<ImagingState> _pub;
    private string _phase = "Idle", _label = "";
    public List<string> Started { get; } = new();
    /// <summary>Starts are refused with this (a mount not connected yet); null = they work.</summary>
    public string? RefuseStarts { get; set; }
    /// <summary>What the imaging service shows by itself (a restart carrying an image on).</summary>
    public async Task ShowAsync(string phase, string label) { _phase = phase; _label = label; if (!Kept.ContainsKey(label)) Kept[label] = (new ImagingRequest { Label = label, TargetSeconds = 3600 }, 0); await _pub.PublishAsync(); }
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
            if (RefuseStarts is { } no) return CommandResult.Fail(no);
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

    /// <summary>The running image ends Done although its thinnest spot is still short of the depth (a frame that never covered the
    /// area's corners, a shot limit).</summary>
    public async Task FinishShortAsync() { _phase = "Done"; await _pub.PublishAsync(); }

    /// <summary>A person presses Stop on the running image.</summary>
    public async Task StoppedByPersonAsync() { _phase = "Aborted"; await _pub.PublishAsync(); }

    public string Phase => _phase;
    public ValueTask DisposeAsync() { _cmds.Dispose(); _pub.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>A mount that can be parked (or refuses to), listed as equipment the way the INDI bridge lists its devices.</summary>
public sealed class ParkableMount : IAsyncDisposable
{
    private readonly CommandSet _cmds;
    private readonly StatePublisher<MountState> _pub;
    private readonly string _id;
    public string Phase { get; private set; } = "Tracking";
    public bool CannotPark { get; set; }
    public int Parks, TrackingStops;

    public ParkableMount(TypeSafeEVentNode node, string id)
    {
        _id = id; _cmds = new CommandSet(node);
        _pub = new(node, EquipmentIds.State(DeviceKinds.Mount, id), EquipmentIds.GetState(DeviceKinds.Mount, id),
            () => new MountState { Connected = true, Phase = Phase, Tracking = Phase == "Tracking", Parked = Phase == "Parked" });
    }

    public async Task StartAsync()
    {
        await _cmds.AddAsync<NOTESVoid, DeviceList>(EquipmentIds.List, _ =>
        {
            var l = new DeviceList(); l.Devices.Add(new DeviceInfo { Id = _id, Kind = DeviceKinds.Mount, DisplayName = _id, Source = "fake" }); return Task.FromResult(l);
        }, "fake device list");
        await _cmds.AddAsync<BinaryConvertibleBool, CommandResult>(EquipmentIds.Command(DeviceKinds.Mount, _id, "Park"), async b =>
        {
            Interlocked.Increment(ref Parks);
            if (CannotPark) return CommandResult.Fail("no park position saved");
            Phase = b.Value ? "Parked" : "Idle"; await _pub.PublishAsync(); return CommandResult.Success();
        }, "fake park");
        await _cmds.AddAsync<BinaryConvertibleBool, CommandResult>(EquipmentIds.Command(DeviceKinds.Mount, _id, "SetTracking"), async b =>
        {
            if (!b.Value) Interlocked.Increment(ref TrackingStops);
            Phase = b.Value ? "Tracking" : "Idle"; await _pub.PublishAsync(); return CommandResult.Success();
        }, "fake tracking");
        await _pub.StartAsync();
    }

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
    public async Task ARunningQueueRunsAgainAfterARestartAndAdoptsTheImageBeingTaken()
    {
        var s = new Schedule { MinRunMinutes = 30 };
        s.Entries.Add(Entry("NCP", 2.5, 85));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Phase.Text: "Imaging", Running.Text: "NCP" }), _last?.Message.Text);

        // the station stops; the image carried on by itself (the imaging service resumes it): the queue takes it up again
        await _scheduler.DisposeAsync();
        _last = null;
        _scheduler = new SchedulerService(_node, _file) { CheckEvery = TimeSpan.FromMilliseconds(300), ResumeDelay = TimeSpan.FromMilliseconds(100) };
        await _scheduler.StartAsync();
        Assert.True(await Eventually(() => _last is { Phase.Text: "Imaging", Running.Text: "NCP" }), $"{_last?.Phase.Text} {_last?.Message.Text}");
        Assert.Equal(new[] { "NCP" }, _imaging.Started);          // adopted, not started a second time

        // stopped by a person: it stays stopped after the next restart
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Stop, NOTESVoid.Void)).Ok.Value);
        await _scheduler.DisposeAsync();
        _last = null;
        _scheduler = new SchedulerService(_node, _file) { CheckEvery = TimeSpan.FromMilliseconds(300), ResumeDelay = TimeSpan.FromMilliseconds(100) };
        await _scheduler.StartAsync();
        await Task.Delay(1500);
        var st = Assert.Single((await _node.CallFunctionAsync<NOTESVoid, SchedulerState>(SchedulerIds.GetState, NOTESVoid.Void))!);
        Assert.Equal("Stopped", st.Phase.Text);
    }

    [Fact]
    public async Task AfterARestartTheQueueWaitsForTheImageBeingCarriedOnAndNeverStopsIt()
    {
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        // the imaging service waits for the mount to carry NCP on; meanwhile a start would fail
        await _imaging.ShowAsync("Resuming", "NCP");
        _imaging.RefuseStarts = "the mount is not connected";
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last?.Message.Text.Contains("carries on after the restart") == true), _last?.Message.Text);
        Assert.Empty(_imaging.Started);
        // even a start that failed before does not make it stop the image once it runs
        await _imaging.ShowAsync("Idle", "");
        Assert.True(await Eventually(() => _last?.Message.Text.Contains("could not start") == true), _last?.Message.Text);
        await _imaging.ShowAsync("Running", "NCP");
        Assert.True(await Eventually(() => _last is { Phase.Text: "Imaging", Running.Text: "NCP" }), _last?.Message.Text);
        await Task.Delay(1500);                                     // several checks
        Assert.Equal(0, _imaging.Aborts);
        Assert.Equal("Running", _imaging.Phase);
    }

    [Fact]
    public async Task AnImageNotFromTheQueueIsLeftToFinish()
    {
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        var mine = new ImagingRequest { Label = "Mine" }; mine.ScopeIds.Add("rig");
        Assert.True((await Commands.CallAsync(_node, ImagingIds.Start, mine)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last?.Message.Text.Contains("not from the queue") == true), _last?.Message.Text);
        Assert.Equal(new[] { "Mine" }, _imaging.Started);
        await _imaging.FinishAsync();
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
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

    [Fact]
    public async Task EntriesThatCanNeverRunAreRefused()
    {
        async Task<string> Refused(Action<ScheduleEntry> change)
        {
            var e = Entry("X", 5, 0); change(e);
            var s = new Schedule(); s.Entries.Add(e);
            var r = await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s);
            Assert.False(r.Ok.Value);
            return r.Error.Text;
        }
        Assert.Contains("not on the sky", await Refused(e => e.Request.Center.DecDegrees = 95));
        Assert.Contains("not a time", await Refused(e => e.NotBeforeUtc = "tomorrow evening"));
        Assert.Contains("ends before it begins", await Refused(e => { e.NotBeforeUtc = "2026-01-15T22:00:00Z"; e.NotAfterUtc = "2026-01-15T21:00:00Z"; }));
        Assert.Contains("longer than zero", await Refused(e => e.Request.Exposure.Seconds = 0));
    }

    [Fact]
    public async Task AnImageThatEndsDoneIsNotStartedAgainEvenWhenItsCoverageIsShort()
    {
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85, priority: 5));
        s.Entries.Add(Entry("M42", 5.588, -5.39));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
        await _imaging.FinishShortAsync();                          // Done, the kept coverage still 0 s
        Assert.True(await Eventually(() => _last is { Running.Text: "M42" }), _last?.Message.Text);
        await Task.Delay(1500);                                     // several checks
        Assert.Equal(new[] { "NCP", "M42" }, _imaging.Started);     // NCP once: it was started again and again before
        Assert.Equal("Complete", _last!.Entries.First(e => e.Label.Text == "NCP").Status.Text);
    }

    [Fact]
    public async Task AnImageAPersonStopsIsLeftUntilTheQueueIsStartedAgain()
    {
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85, priority: 5));
        s.Entries.Add(Entry("M42", 5.588, -5.39));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
        await _imaging.StoppedByPersonAsync();
        Assert.True(await Eventually(() => _last is { Running.Text: "M42" }), _last?.Message.Text);   // it goes on with the next
        Assert.Equal("stopped by hand", _last!.Entries.First(e => e.Label.Text == "NCP").Reason.Text);
        await _imaging.StoppedByPersonAsync();                      // that one too: nothing is left to take
        Assert.True(await Eventually(() => _last is { Running.Text: "" }), _last?.Message.Text);
        await Task.Delay(1500);
        Assert.Equal(new[] { "NCP", "M42" }, _imaging.Started);
        // starting the queue again takes them up
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _imaging.Started.Count == 3), string.Join(",", _imaging.Started));
        Assert.Equal("NCP", _imaging.Started[2]);
    }

    [Fact]
    public async Task TheMountIsParkedOnceEverythingIsDone()
    {
        await using var mount = new ParkableMount(_node, "OnStep"); await mount.StartAsync();
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
        await Task.Delay(1000);
        Assert.Equal(0, mount.Parks);                                // not while it works
        await _imaging.FinishAsync();
        Assert.True(await Eventually(() => mount.Phase == "Parked"), $"{mount.Phase} {_last?.Message.Text}");
        Assert.True(await Eventually(() => _last?.Message.Text.Contains("parked") == true), _last?.Message.Text);
        await Task.Delay(1500);                                      // several checks
        Assert.Equal(1, mount.Parks);                                // once, not on every check
    }

    [Fact]
    public async Task WhenTheSunIsUpTheImageIsStoppedAndTheMountParked()
    {
        await using var mount = new ParkableMount(_node, "OnStep"); await mount.StartAsync();
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85));                         // always up, and not asked to need the dark
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
        // the morning comes: the Sun rises over a few checks
        foreach (var t in new[] { "06:40", "07:00", "07:20", "07:40", "08:00" })
        {
            _now = DateTime.Parse("2026-01-16T" + t + ":00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            await Task.Delay(800);
        }
        Assert.True(await Eventually(() => mount.Phase == "Parked"), $"{mount.Phase} {_last?.Message.Text}");
        Assert.True(_imaging.Aborts >= 1);
        Assert.Equal("", _last!.Running.Text);
    }

    [Fact]
    public async Task AMountThatCannotParkHasItsTrackingStopped()
    {
        await using var mount = new ParkableMount(_node, "OnStep") { CannotPark = true }; await mount.StartAsync();
        var s = new Schedule();
        s.Entries.Add(Entry("NCP", 2.5, 85));
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.SetSchedule, s)).Ok.Value);
        Assert.True((await Commands.CallAsync(_node, SchedulerIds.Start, NOTESVoid.Void)).Ok.Value);
        Assert.True(await Eventually(() => _last is { Running.Text: "NCP" }), _last?.Message.Text);
        await _imaging.FinishAsync();
        Assert.True(await Eventually(() => mount.TrackingStops == 1), $"{mount.Phase} {_last?.Message.Text}");
        Assert.Equal("Idle", mount.Phase);
    }
}
