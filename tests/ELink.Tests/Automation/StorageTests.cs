using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ELink.Automation;
using ELink.Compose;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using ELink.Tests.Compose;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Automation;

public class StorageTests : IAsyncLifetime
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private TypeSafeEVentNode _node = null!;
    private string _dir = "";

    public Task InitializeAsync()
    {
        _node = ElinkNode.Create("ST-" + Guid.NewGuid().ToString("N")[..6], FreePort());
        _dir = Path.Combine(Path.GetTempPath(), "elink-store-" + Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _node.Dispose(); try { Directory.Delete(_dir, true); } catch { } return Task.CompletedTask; }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private Task Fire(string shooter, string format = ".fits", double seconds = 30, string filter = "Blue", double ra = 5.5, double dec = -3) =>
        _node.FireEventAsync(ShooterIds.Shot(shooter), new ShotEvent
        {
            Shooter = shooter, Format = format, ExposureSeconds = seconds, FrameType = "Light", Filter = filter,
            PointingRaHours = ra, PointingDecDegrees = dec, Timestamp = DateTime.UtcNow.ToString("o"),
            Data = new RawBytes(format == ".fits" ? FitsImage.Write16(16, 16, new ushort[256]) : new byte[] { 9, 9, 9 }),
        });

    private Task<CommandResult> Watch(string id, bool on = true) => Commands.CallAsync(_node, StorageIds.Watch, new StorageWatch { ShooterId = id, Enabled = on });

    [Fact]
    public async Task SavesFitsWithHeadersAndALogAndAnnouncesIt()
    {
        await using var svc = new StorageService(_node, _dir) { LocalNow = () => new DateTime(2026, 10, 2, 1, 30, 0) };
        await svc.StartAsync();
        FrameSaved? saved = null;
        await _node.HookEventAsync(StorageIds.Saved, (FrameSaved f) => saved = f);
        Assert.True((await Watch("cam1")).Ok.Value);

        await Fire("cam1"); await Fire("cam1");
        Assert.True(await Eventually(() => svc.FramesSaved == 2));
        string night = Path.Combine(_dir, "2026-10-01");                      // 01:30 belongs to the previous evening's night
        var files = Directory.GetFiles(night, "*.fits").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "Light_Blue_30s_0001.fits", "Light_Blue_30s_0002.fits" }, files);

        var img = FitsImage.Parse(File.ReadAllBytes(Path.Combine(night, files[0]!)));
        Assert.Equal(30, img.GetDouble("EXPTIME"));
        Assert.Equal("Blue", img.Get("FILTER"));
        Assert.Equal("cam1", img.Get("ELINKSRC"));
        Assert.Equal(5.5 * 15, img.GetDouble("RA"), 6);
        Assert.Equal(-3, img.GetDouble("DEC"), 6);

        var log = File.ReadAllLines(Path.Combine(night, "session.jsonl"));
        Assert.Equal(2, log.Length);
        using (var doc = JsonDocument.Parse(log[0])) { Assert.Equal("2026-10-01/Light_Blue_30s_0001.fits".Replace('/', Path.DirectorySeparatorChar), doc.RootElement.GetProperty("file").GetString()); }
        Assert.True(await Eventually(() => saved is not null && saved.Path.Text.EndsWith("0002.fits")));
        var state = (await _node.CallFunctionAsync<Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid, StorageState>(StorageIds.GetState, Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid.Void))!.Single();
        Assert.Equal(2, state.FramesSaved.Value); Assert.Single(state.Watching);
    }

    [Fact]
    public async Task NightFolderChangesAtNoonAndNumbersContinueAfterARestart()
    {
        DateTime now = new(2026, 10, 2, 13, 0, 0);
        await using (var svc = new StorageService(_node, _dir) { LocalNow = () => now })
        {
            await svc.StartAsync(); await Watch("c");
            await Fire("c"); Assert.True(await Eventually(() => svc.FramesSaved == 1));
            Assert.True(File.Exists(Path.Combine(_dir, "2026-10-02", "Light_Blue_30s_0001.fits")));   // 13:00 is the next night
        }
        using var node2 = ElinkNode.Create("ST-again", FreePort());
        await using var again = new StorageService(node2, _dir) { LocalNow = () => now };
        await again.StartAsync();
        await Commands.CallAsync(node2, StorageIds.Watch, new StorageWatch { ShooterId = "c" });
        await node2.FireEventAsync(ShooterIds.Shot("c"), new ShotEvent { Shooter = "c", Format = ".fits", ExposureSeconds = 30, FrameType = "Light", Filter = "Blue", Data = new RawBytes(FitsImage.Write16(8, 8, new ushort[64])) });
        Assert.True(await Eventually(() => again.FramesSaved == 1));
        Assert.True(File.Exists(Path.Combine(_dir, "2026-10-02", "Light_Blue_30s_0002.fits")));     // never overwrites
    }

    [Fact]
    public async Task NonFitsFramesAreStoredAsReceivedAndWatchCanBeSwitchedOff()
    {
        await using var svc = new StorageService(_node, _dir); await svc.StartAsync();
        await Watch("cam");
        await Fire("cam", ".jpg", 1, "");
        Assert.True(await Eventually(() => svc.FramesSaved == 1));
        var jpg = Directory.GetFiles(_dir, "*.jpg", SearchOption.AllDirectories).Single();
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(jpg));
        Assert.StartsWith("Light_1s_", Path.GetFileName(jpg));                                         // no filter, no empty segment

        await Watch("cam", false);
        await Fire("cam"); await Task.Delay(300);
        Assert.Equal(1, svc.FramesSaved);
        Assert.False((await Watch("")).Ok.Value);
    }

    [Fact]
    public async Task ChangingTheDirectoryAndRejectingAnUnusableOne()
    {
        string other = Path.Combine(_dir, "other");
        await using var svc = new StorageService(_node, _dir); await svc.StartAsync();
        Assert.True((await Commands.CallAsync(_node, StorageIds.SetDirectory, (BinaryConvertibleString)other)).Ok.Value);
        Assert.False((await Commands.CallAsync(_node, StorageIds.SetDirectory, (BinaryConvertibleString)"")).Ok.Value);
        await Watch("cam"); await Fire("cam");
        Assert.True(await Eventually(() => svc.FramesSaved == 1));
        Assert.Single(Directory.GetFiles(other, "*.fits", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FramesOfARunningSequenceAreNamedAfterItsBlock()
    {
        var pointer = new FakePointer(_node, "sp", 50); await pointer.StartAsync();
        var shooter = new FakeShooter(_node, "ss") { Fits = true }; await shooter.StartAsync();
        var def = new ScopeDefinition { Id = "stscope" }; def.Pointers.Add("sp"); def.Shooters.Add(new ScopeShooterRef { Id = "ss" });
        await using var scope = new SmartScope(_node, def); await scope.StartAsync();
        await using var seq = new SequencerService(_node); await seq.StartAsync();
        await using var svc = new StorageService(_node, _dir); await svc.StartAsync();
        await Watch("stscope");

        var plan = new SequencePlan { Id = "night1", ScopeId = "stscope" };
        plan.Blocks.Add(new SequenceBlock { Label = "M 42", Count = 2, Target = new SkyTarget { RaHours = 5.6, DecDegrees = -5, Epoch = "J2000" }, Exposure = new ShooterExposure { Seconds = 0.1, Filter = "Ha" } });
        Assert.True((await Commands.CallAsync(_node, SequencerIds.Start, plan)).Ok.Value);
        Assert.True(await Eventually(() => svc.FramesSaved == 2), $"saved {svc.FramesSaved}");
        var files = Directory.GetFiles(_dir, "*.fits", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "M_42_Light_Ha_0.1s_0001.fits", "M_42_Light_Ha_0.1s_0002.fits" }, files);
        var img = FitsImage.Parse(File.ReadAllBytes(Directory.GetFiles(_dir, "*0001.fits", SearchOption.AllDirectories).Single()));
        Assert.Equal("M 42", img.Get("OBJECT")); Assert.Equal("night1", img.Get("ELINKPLN")); Assert.Equal("Ha", img.Get("FILTER"));
        Assert.Equal(5.6 * 15, img.GetDouble("RA"), 4);                                               // the scope added its pointing
    }
}
