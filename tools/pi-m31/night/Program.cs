// A stand-in for the desktop window on a laptop: joins the Pi station's mesh and does what a person would do in the
// UI (connect, set up the 900 mm scope, frame M 31 wide, start, watch). It hosts no backend; it only calls the Pi.
//   night HOST PORT setup|preview|start [stack]|watch SECONDS|pause|resume|abort|state|saved|profile LABEL INDIPORT DRIVER...
using System.Diagnostics;
using System.Globalization;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Contracts.Site;
using ELink.Core;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
string host = args[0]; int port = int.Parse(args[1]); string verb = args[2];
using var node = ElinkNode.Create("laptop-" + Guid.NewGuid().ToString("N")[..5], 0);
var join = await ElinkNode.JoinAsync(node, host, port);
Log($"join {host}:{port}: {join}");

const string Mount = "Telescope_Simulator", Camera = "CCD_Simulator", Train = "t900", Scope = "home900";

switch (verb)
{
    case "setup": await Setup(); break;
    case "preview": Print(await Preview(Request(stack: false))); break;
    case "start":
        {
            bool stack = args.Length > 3 && args[3] == "stack";
            var w = await Commands.CallAsync(node, StorageIds.Watch, new StorageWatch { ShooterId = Scope, Enabled = true });
            Log($"storage watch {Scope}: {w.Ok.Value} {w.Error.Text}");
            var sw = Stopwatch.StartNew();
            var r = await Commands.CallAsync(node, ImagingIds.Start, Request(stack), TimeSpan.FromSeconds(240));
            Log($"start ({(stack ? "live stack ON" : "live stack off")}): {r.Ok.Value} {r.Error.Text} in {sw.Elapsed.TotalSeconds:0.0}s");
            break;
        }
    case "watch": await Watch(double.Parse(args[3])); break;
    case "state": Print(await GetState()); break;
    case "pause": case "resume": case "abort":
        {
            string id = verb switch { "pause" => ImagingIds.Pause, "resume" => ImagingIds.Resume, _ => ImagingIds.Abort };
            var r = await Commands.CallAsync(node, id, NOTESVoid.Void);
            Log($"{verb}: {r.Ok.Value} {r.Error.Text}");
            break;
        }
    case "profile":
        {
            // an equipment profile: the station runs indiserver with these drivers and connects the devices
            var p = new EquipmentProfile { Label = args[3], Port = int.Parse(args[4]), AutoConnect = true };
            foreach (var d in args.Skip(5)) p.Drivers.Add(d);
            Log($"save profile {args[3]}: {Result(await Commands.CallAsync(node, ProfileIds.Save, p))}");
            Log($"start profile {args[3]}: {Result(await Commands.CallAsync(node, ProfileIds.Start, (BinaryConvertibleString)args[3], TimeSpan.FromSeconds(60)))}");
            break;
        }
    case "saved":
        {
            var s = await node.CallFunctionAsync<NOTESVoid, StorageState>(StorageIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(10));
            var st = s!.First(); Log($"storage {st.Directory.Text}: {st.FramesSaved.Value} saved, last {st.LastFile.Text}");
            break;
        }
}
return 0;

ImagingRequest Request(bool stack)
{
    // M 31, the field of a 150 mm lens on APS-C (23.5 x 15.6 mm -> 8.97° x 5.95°), turned along the galaxy; 30 s unguided subs;
    // the picture at 9.3"/px, about 3470 x 2300 = 8 MP
    var r = new ImagingRequest
    {
        Label = "M31 wide", Center = new SkyTarget { RaHours = 0.7123, DecDegrees = 41.269, Epoch = "J2000" },
        WidthDegrees = 8.97, HeightDegrees = 5.95, PositionAngleDegrees = 35,
        Exposure = new ShooterExposure { Seconds = 30, FrameType = "Light" }, TargetSeconds = 600, DitherArcsec = 30,
        LiveStack = stack, OutputPixelScaleArcsec = 9.3, Resume = true,
    };
    r.ScopeIds.Add(Scope);
    return r;
}

async Task Setup()
{
    // the equipment, as the Devices page would see it
    for (int i = 0; ; i++)
    {
        var all = (await node.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void, TimeSpan.FromSeconds(10)))!.SelectMany(l => l.Devices).ToList();
        if (all.Any(d => d.Kind.Text == "Mount" && d.Id.Text == Mount) && all.Any(d => d.Kind.Text == "Camera" && d.Id.Text == Camera)) break;
        if (i > 60) throw new Exception("no simulator devices: " + string.Join(", ", all.Select(d => d.Kind.Text + ":" + d.Id.Text)));
        await Task.Delay(1000);
    }
    foreach (var (k, id) in new[] { ("Mount", Mount), ("Camera", Camera) })
        Log($"connect {k} {id}: {(await Commands.CallAsync(node, EquipmentIds.Command(k, id, "Connect"), (BinaryConvertibleBool)true, TimeSpan.FromSeconds(60))).Ok.Value}");
    Log($"unpark: {(await Commands.CallAsync(node, EquipmentIds.Command("Mount", Mount, "Park"), (BinaryConvertibleBool)false, TimeSpan.FromSeconds(60))).Ok.Value}");

    // the site (where the M 31 test frames were taken), pushed to the mount
    var site = new SiteConfig { Label = "Home", LatitudeDegrees = 47.485, LongitudeDegrees = 19.0625, ElevationMeters = 120, PushToMounts = true, MinAltitudeDegrees = 15 };
    Log($"site: {(await Commands.CallAsync(node, SiteIds.Configure(SiteIds.Default), site)).Ok.Value}");

    // Quick set up: the telescope (900 mm + the APS-C camera) and an unguided scope on the mount
    Log($"mount pointer: {Result(await Commands.CallAsync(node, ScopeIds.DefineMountPointer, new MountPointerDefinition { Id = "mount", MountId = Mount }))}");
    var train = new ImagingTrainDefinition { Id = Train, Label = "900 mm + 24 MP APS-C", FocalLengthMm = 900, ApertureMm = 130 };
    train.Cameras.Add(new TrainCamera { CameraId = Camera, Role = "Imaging" });
    Log($"telescope: {Result(await Commands.CallAsync(node, ScopeIds.DefineTrain, train))}");
    var scope = new ScopeDefinition
    {
        Id = Scope, DisplayName = "Home 900", GradeFrames = true, CenterAfterSlew = false, FocusOnStart = false, MeridianFlip = true, SiteId = SiteIds.Default,
    };
    scope.Pointers.Add("mount"); scope.Shooters.Add(new ScopeShooterRef { Id = Train });
    Log($"scope: {Result(await Commands.CallAsync(node, ScopeIds.Define, scope))}");
    await Task.Delay(3000);
    var ts = (await node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState(Train), NOTESVoid.Void, TimeSpan.FromSeconds(10)))?.FirstOrDefault();
    foreach (var c in ts?.Cameras ?? new()) Log($"camera {c.CameraId.Text}: {c.PixelScaleArcsec.Value:0.000}\"/px, {c.FieldWidthDegrees.Value:0.000}° x {c.FieldHeightDegrees.Value:0.000}°, connected {c.Connected.Value}");
}

async Task<ImagingState> Preview(ImagingRequest r)
{
    var sw = Stopwatch.StartNew();
    var p = (await node.CallFunctionAsync<ImagingRequest, ImagingState>(ImagingIds.Preview, r, TimeSpan.FromSeconds(120)))!.First();
    Log($"preview in {sw.Elapsed.TotalSeconds:0.00}s");
    return p;
}

async Task<ImagingState> GetState() => (await node.CallFunctionAsync<NOTESVoid, ImagingState>(ImagingIds.GetState, NOTESVoid.Void, TimeSpan.FromSeconds(20)))!.First();

void Print(ImagingState s)
{
    Log($"phase {s.Phase.Text} '{s.Label.Text}': {s.Message.Text}");
    Log($"  area {s.WidthDegrees.Value:0.00}° x {s.HeightDegrees.Value:0.00}°, map {s.MapCols.Value} x {s.MapRows.Value} cells, {s.Map.Data.Length} bytes; shots {s.Visits.Value}");
    Log($"  depth: min {s.MinSeconds.Value:0} s, mean {s.MeanSeconds.Value:0.0} s, max {s.MaxSeconds.Value:0} s of {s.TargetSeconds.Value:0} s");
    foreach (var w in s.Workers)
        Log($"  {w.ScopeId.Text}: {w.Phase.Text} '{w.Message.Text}' good {w.Visits.Value} rejected {w.Rejected.Value} at ({w.PoseX.Value:0.00}, {w.PoseY.Value:0.00})°; frames " +
            string.Join(" + ", w.Frames.Select(f => $"{f.WidthDegrees.Value:0.000}°x{f.HeightDegrees.Value:0.000}° rot {f.RotationDegrees.Value:0.0}")));
    if (s.Map.Data.Length == s.MapCols.Value * s.MapRows.Value && s.MapCols.Value > 0)
    {
        // a small picture of the coverage: ' ' none, '.' some, 'o' half, '#' full
        int cols = s.MapCols.Value, rows = s.MapRows.Value, step = Math.Max(1, cols / 60);
        for (int y = 0; y < rows; y += step * 2)
        {
            var line = new char[(cols + step - 1) / step];
            for (int x = 0, k = 0; x < cols; x += step, k++) { int v = s.Map.Data[y * cols + x]; line[k] = v == 0 ? ' ' : v < 64 ? '.' : v < 128 ? ':' : v < 255 ? 'o' : '#'; }
            Log("  |" + new string(line) + "|");
        }
    }
}

async Task Watch(double seconds)
{
    ImagingState? last = null; int lastVisits = -1; var lastShot = DateTime.UtcNow;
    await node.HookEventAsync(ImagingIds.State, (ImagingState s) => last = s);
    var until = DateTime.UtcNow.AddSeconds(seconds);
    var polled = DateTime.MinValue;
    while (DateTime.UtcNow < until)
    {
        // the link may drop (Wi-Fi): poll as well, rejoin, never give up
        if (DateTime.UtcNow - polled > TimeSpan.FromSeconds(20))
        {
            polled = DateTime.UtcNow;
            try { last = await GetState(); }
            catch (Exception ex) { Log($"(no answer: {ex.Message}; rejoining)"); try { await ElinkNode.JoinAsync(node, host, port); } catch { } }
        }
        var s = last;
        if (s is not null && s.Visits.Value != lastVisits)
        {
            var now = DateTime.UtcNow;
            var w = s.Workers.FirstOrDefault();
            Log($"shot {s.Visits.Value,4} (+{(now - lastShot).TotalSeconds,5:0.0}s) {s.Phase.Text} min {s.MinSeconds.Value:0} mean {s.MeanSeconds.Value:0.0} max {s.MaxSeconds.Value:0} | {w?.Phase.Text} rej {w?.Rejected.Value} at ({w?.PoseX.Value:0.00},{w?.PoseY.Value:0.00}) {w?.Message.Text} {s.Message.Text}");
            lastVisits = s.Visits.Value; lastShot = now;
        }
        await Task.Delay(500);
    }
    try { Print(await GetState()); } catch (Exception ex) { Log("final state: " + ex.Message); }
}

static string Result(CommandResult r) => r.Ok.Value ? "ok" : "FAILED " + r.Error.Text;
static void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
