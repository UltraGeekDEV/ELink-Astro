using System.Net;
using System.Net.Sockets;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.IndiBridge;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Indi;

[Collection("indiserver")]
public class TypedBridgeTests(IndiServerFixture server) : IAsyncLifetime
{
    private static int FreePort() => ELink.Testing.TestPorts.Next();
    private TypeSafeEVentNode _bridge = null!, _consumer = null!;
    private IndiServerLink _link = null!;

    public async Task InitializeAsync()
    {
        Assert.True(server.Available);
        int port = FreePort();
        _bridge = ElinkNode.Create("TB-Bridge", port);
        var dir = new DeviceDirectory(_bridge);
        await dir.StartAsync();
        _link = new IndiServerLink(_bridge, dir, "sim", "127.0.0.1", server.Port);
        await _link.StartAsync();
        _consumer = ElinkNode.Create("TB-Consumer", FreePort());
        Assert.Equal(MergeResult.Connected, await ElinkNode.JoinAsync(_consumer, "127.0.0.1", port));
    }

    public async Task DisposeAsync()
    {
        await _link.DisposeAsync();
        _consumer.Dispose();
        _bridge.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(25); }
        return cond();
    }

    private async Task<List<DeviceInfo>> WaitForDevices(params string[] kinds)
    {
        List<DeviceInfo> all = new();
        await Eventually(() =>
        {
            var answers = _consumer.CallFunctionAsync<NOTESVoid, DeviceList>(EquipmentIds.List, NOTESVoid.Void).GetAwaiter().GetResult();
            all = answers!.SelectMany(a => a.Devices).ToList();
            return kinds.All(k => k.Contains('/') ? all.Any(d => d.Kind.Text + "/" + d.Id.Text == k) : all.Any(d => d.Kind.Text == k));
        });
        return all;
    }

    private async Task<CommandResult> Cmd<TIn>(string kind, string id, string command, TIn input) where TIn : IBinaryConvertible, new()
    {
        var answers = await _consumer.CallFunctionAsync<TIn, CommandResult>(EquipmentIds.Command(kind, id, command), input);
        return Assert.Single(answers!);
    }

    private async Task<Func<T?>> Watch<T>(string kind, string id) where T : IBinaryConvertible, new()
    {
        T? last = default;
        await _consumer.HookEventAsync(EquipmentIds.State(kind, id), (T s) => last = s);
        // state events fire on change only: seed with the current state like any late joiner
        var now = await _consumer.CallFunctionAsync<NOTESVoid, T>(EquipmentIds.GetState(kind, id), NOTESVoid.Void);
        last ??= now!.Single();
        return () => last;
    }

    [Fact]
    public async Task DevicesAreDiscoveredByKind()
    {
        var devices = await WaitForDevices("Mount", "Camera", "Focuser", "FilterWheel", "Rotator");
        Assert.Contains(devices, d => d.Kind.Text == "Mount" && d.Id.Text == "Telescope_Simulator" && d.Source.Text == "indi:sim");
        Assert.Contains(devices, d => d.Kind.Text == "Camera" && d.DisplayName.Text == "CCD Simulator");
    }

    [Fact]
    public async Task MountSlewsInJ2000AndReportsState()
    {
        await WaitForDevices("Mount");
        var state = await Watch<MountState>("Mount", "Telescope_Simulator");
        Assert.True((await Cmd("Mount", "Telescope_Simulator", "Connect", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.True(await Eventually(() => state()?.Connected.Value == true));

        var goto_ = await Cmd("Mount", "Telescope_Simulator", "Goto", new SkyTarget { RaHours = 10.0, DecDegrees = 30.0, Epoch = "J2000" });
        Assert.True(goto_.Ok.Value, goto_.Error.Text);
        Assert.True(await Eventually(() => state()?.Phase.Text == "Slewing"));
        Assert.True(await Eventually(() => state()?.Phase.Text == "Tracking", 90000));
        // the simulator speaks JNow; the bridge converted J2000 -> JNow, so we landed a bit east of 10h
        Assert.Equal("JNow", state()!.Epoch.Text);
        Assert.InRange(state()!.RaHours.Value, 10.0, 10.1);

        Assert.False((await Cmd("Mount", "Telescope_Simulator", "Goto", new SkyTarget { RaHours = 1, DecDegrees = 120, Epoch = "J2000" })).Ok.Value);
        Assert.True((await Cmd("Mount", "Telescope_Simulator", "SetTracking", (BinaryConvertibleBool)false)).Ok.Value);
        Assert.True(await Eventually(() => state()?.Tracking.Value == false));
        Assert.True((await Cmd("Mount", "Telescope_Simulator", "Park", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.True(await Eventually(() => state()?.Phase.Text == "Parked", 60000));
    }

    [Fact]
    public async Task CameraDeliversFitsFrames()
    {
        await WaitForDevices("Camera");
        var state = await Watch<CameraState>("Camera", "CCD_Simulator");
        FrameEvent? frame = null;
        await _consumer.HookEventAsync(EquipmentIds.Command("Camera", "CCD_Simulator", "Frame"), (FrameEvent f) => frame = f);
        Assert.True((await Cmd("Camera", "CCD_Simulator", "Connect", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.True(await Eventually(() => state()?.Connected.Value == true));
        Assert.Equal(1280, state()!.SensorWidth.Value);

        var r = await Cmd("Camera", "CCD_Simulator", "Expose", new ExposeRequest { Seconds = 1, FrameType = "Light" });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => frame is not null, 30000));
        Assert.Equal(".fits", frame!.Format.Text);
        Assert.StartsWith("SIMPLE", System.Text.Encoding.ASCII.GetString(frame.Data.Data, 0, 6));
        Assert.True(await Eventually(() => state()?.Phase.Text == "Idle"));
        Assert.False((await Cmd("Camera", "CCD_Simulator", "Expose", new ExposeRequest { Seconds = 1, FrameType = "Nonsense" })).Ok.Value);

        // gain and offset go with an exposure; the cooler takes a set point
        frame = null;
        Assert.True((await Cmd("Camera", "CCD_Simulator", "Expose", new ExposeRequest { Seconds = 0.5, Gain = 33, Offset = 12 })).Ok.Value);
        Assert.True(await Eventually(() => frame is not null, 30000));
        Assert.True(await Eventually(() => state()?.Gain.Value == 33 && state()?.Offset.Value == 12), $"gain {state()?.Gain.Value} offset {state()?.Offset.Value}");
        Assert.True(state()!.HasCooler.Value);
        Assert.True((await Cmd("Camera", "CCD_Simulator", "SetCooler", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.True((await Cmd("Camera", "CCD_Simulator", "SetTemperature", (BinaryConvertibleDouble)(-5.0))).Ok.Value);
        Assert.True(await Eventually(() => state()?.TemperatureTarget.Value == -5), $"{state()?.TemperatureTarget.Value}");   // (the simulator ignores its own cooler switch)
    }

    [Fact]
    public async Task DomeWeatherAndGpsAreBridged()
    {
        await WaitForDevices("Dome", "Weather", "Gps");
        var dome = await Watch<DomeState>("Dome", "Dome_Simulator");
        var weather = await Watch<WeatherState>("Weather", "Weather_Simulator");
        var gps = await Watch<GpsState>("Gps", "GPS_Simulator");
        foreach (var (k, i) in new[] { ("Dome", "Dome_Simulator"), ("Weather", "Weather_Simulator"), ("Gps", "GPS_Simulator") })
            Assert.True((await Cmd(k, i, "Connect", (BinaryConvertibleBool)true)).Ok.Value);

        Assert.True(await Eventually(() => gps()?.Connected.Value == true && !double.IsNaN(gps()!.LatitudeDegrees.Value)));
        Assert.Equal(51, gps()!.LatitudeDegrees.Value, 3);
        Assert.InRange(gps()!.LongitudeDegrees.Value, -3, -2);          // 357.7 east = 2.3 west
        Assert.True(await Eventually(() => weather()?.Connected.Value == true && weather()!.Parameters.Count >= 5));
        Assert.Contains(weather()!.Parameters, p => p.Id.Text == "WEATHER_TEMPERATURE" && p.Value.Value == 15);

        Assert.True(await Eventually(() => dome()?.Connected.Value == true));
        Assert.True((await Cmd("Dome", "Dome_Simulator", "GotoAzimuth", (BinaryConvertibleDouble)(-90.0))).Ok.Value);   // normalised to 270
        Assert.True(await Eventually(() => Math.Abs(dome()!.AzimuthDegrees.Value - 270) < 0.5 && !dome()!.Moving.Value, 90000));
        Assert.True((await Cmd("Dome", "Dome_Simulator", "Shutter", (BinaryConvertibleBool)true)).Ok.Value);
        Assert.True(await Eventually(() => dome()?.Shutter.Text == "Open", 60000));
        Assert.True((await Cmd("Dome", "Dome_Simulator", "Shutter", (BinaryConvertibleBool)false)).Ok.Value);
        Assert.True(await Eventually(() => dome()?.Shutter.Text == "Closed", 60000));
    }

    [Fact]
    public async Task FocuserWheelAndRotatorMove()
    {
        await WaitForDevices("Focuser", "FilterWheel/Filter_Simulator", "Rotator");
        var foc = await Watch<FocuserState>("Focuser", "Focuser_Simulator");
        var wheel = await Watch<FilterWheelState>("FilterWheel", "Filter_Simulator");
        var rot = await Watch<RotatorState>("Rotator", "Rotator_Simulator");
        foreach (var (k, i) in new[] { ("Focuser", "Focuser_Simulator"), ("FilterWheel", "Filter_Simulator"), ("Rotator", "Rotator_Simulator") })
            Assert.True((await Cmd(k, i, "Connect", (BinaryConvertibleBool)true)).Ok.Value);

        Assert.True((await Cmd("Focuser", "Focuser_Simulator", "MoveTo", (BinaryConvertibleInt32)51000)).Ok.Value);
        Assert.True(await Eventually(() => foc()?.Position.Value == 51000 && !foc()!.Moving.Value, 60000));
        Assert.False((await Cmd("Focuser", "Focuser_Simulator", "MoveTo", (BinaryConvertibleInt32)9_999_999)).Ok.Value);

        Assert.True(await Eventually(() => wheel()?.FilterNames.Count > 3));
        Assert.True((await Cmd("FilterWheel", "Filter_Simulator", "SelectFilter", (BinaryConvertibleString)"Blue")).Ok.Value);
        Assert.True(await Eventually(() => wheel()?.Slot.Value == 3 && !wheel()!.Moving.Value, 30000));
        Assert.False((await Cmd("FilterWheel", "Filter_Simulator", "SelectFilter", (BinaryConvertibleString)"Purple")).Ok.Value);

        Assert.True((await Cmd("Rotator", "Rotator_Simulator", "MoveTo", (BinaryConvertibleDouble)45.0)).Ok.Value);
        Assert.True(await Eventually(() => Math.Abs(rot()?.AngleDegrees.Value - 45.0 ?? 99) < 0.5 && !rot()!.Moving.Value, 60000));
    }
}
