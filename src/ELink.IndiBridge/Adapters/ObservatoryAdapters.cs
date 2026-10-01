using ELink.Contracts.Equipment;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

public sealed class DomeAdapter(AdapterContext ctx) : IndiDeviceAdapter<DomeState>(ctx)
{
    public override string Kind => DeviceKinds.Dome;

    protected override DomeState BuildState()
    {
        var s = new DomeState { Connected = Connected };
        if (!Connected) return s;
        var abs = P("ABS_DOME_POSITION");
        if (abs is not null) s.AzimuthDegrees = abs.Number("DOME_ABSOLUTE_POSITION");
        s.Moving = abs?.State == IndiState.Busy || P("DOME_MOTION")?.State == IndiState.Busy || P("REL_DOME_POSITION")?.State == IndiState.Busy;
        var shutter = P("DOME_SHUTTER");
        s.Shutter = shutter is null ? "Unknown"
            : shutter.Switch("SHUTTER_OPEN") ? (shutter.State == IndiState.Busy ? "Opening" : "Open")
            : shutter.Switch("SHUTTER_CLOSE") ? (shutter.State == IndiState.Busy ? "Closing" : "Closed") : "Unknown";
        var park = P("DOME_PARK");
        s.Parked = park?.Switch("PARK") == true && park.State == IndiState.Ok;
        if (abs?.State == IndiState.Alert || shutter?.State == IndiState.Alert) s.Message = "the dome reported an error";
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<BinaryConvertibleDouble, CommandResult>("GotoAzimuth", GotoAsync, "turn the dome to an azimuth in degrees (0 = north, east positive)");
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("Shutter", ShutterAsync, "open (true) or close (false) the shutter");
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("Park", ParkAsync, "park (true) or unpark (false) the dome");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Abort", _ => AbortAsync(), "stop all dome motion");
    }

    private async Task<CommandResult> GotoAsync(BinaryConvertibleDouble az)
    {
        if (Need("ABS_DOME_POSITION", "DOME_ABSOLUTE_POSITION") is { } missing) return missing;
        if (double.IsNaN(az.Value)) return CommandResult.Fail("invalid azimuth");
        double a = ((az.Value % 360) + 360) % 360;
        return await Send(() => SetNumber("ABS_DOME_POSITION", "DOME_ABSOLUTE_POSITION", a));
    }

    private async Task<CommandResult> ShutterAsync(BinaryConvertibleBool open)
    {
        string element = open.Value ? "SHUTTER_OPEN" : "SHUTTER_CLOSE";
        if (Need("DOME_SHUTTER", element) is { } missing) return missing;
        return await Send(() => SetSwitch("DOME_SHUTTER", element));
    }

    private async Task<CommandResult> ParkAsync(BinaryConvertibleBool park)
    {
        string element = park.Value ? "PARK" : "UNPARK";
        if (Need("DOME_PARK", element) is { } missing) return missing;
        return await Send(() => SetSwitch("DOME_PARK", element));
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("DOME_ABORT_MOTION", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("DOME_ABORT_MOTION", "ABORT"));
    }
}

public sealed class WeatherAdapter(AdapterContext ctx) : IndiDeviceAdapter<WeatherState>(ctx)
{
    public override string Kind => DeviceKinds.Weather;

    protected override WeatherState BuildState()
    {
        var s = new WeatherState { Connected = Connected };
        if (!Connected) return s;
        // INDI weather: Ok = safe, Busy = warning, Alert = unsafe, Idle = not known yet
        var safety = P("SAFETY_STATUS");
        (s.Safety, s.Safe) = safety?.State switch
        {
            IndiState.Ok => ("Safe", true), IndiState.Busy => ("Warning", false), IndiState.Alert => ("Unsafe", false), _ => ("Unknown", false),
        };
        var values = P("WEATHER_PARAMETERS");
        var status = P("WEATHER_STATUS");
        if (values is not null)
            foreach (var e in values.Elements)
            {
                var st = status?[e.Name]?.AsLight();
                s.Parameters.Add(new WeatherParameter
                {
                    Id = e.Name, Label = e.Label, Value = e.AsNumber(),
                    Status = st switch { IndiState.Alert => "Alert", IndiState.Busy => "Warning", _ => "Ok" },
                });
            }
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Refresh", async _ =>
        {
            if (Need("WEATHER_REFRESH", "REFRESH") is { } missing) return missing;
            return await Send(() => SetSwitch("WEATHER_REFRESH", "REFRESH"));
        }, "read the sensors now");
    }
}

public sealed class GpsAdapter(AdapterContext ctx) : IndiDeviceAdapter<GpsState>(ctx)
{
    public override string Kind => DeviceKinds.Gps;

    protected override GpsState BuildState()
    {
        var s = new GpsState { Connected = Connected };
        if (!Connected) return s;
        if (P("GEOGRAPHIC_COORD") is { } g)
        {
            s.HasFix = g.State == IndiState.Ok;
            s.LatitudeDegrees = g.Number("LAT");
            double lon = g.Number("LONG");
            s.LongitudeDegrees = double.IsNaN(lon) ? lon : lon > 180 ? lon - 360 : lon;   // INDI longitude is 0..360
            s.ElevationMeters = g.Number("ELEV");
        }
        if (P("TIME_UTC") is { } t) s.TimeUtc = t.Text("UTC");
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Refresh", async _ =>
        {
            if (Need("GPS_REFRESH", "REFRESH") is { } missing) return missing;
            return await Send(() => SetSwitch("GPS_REFRESH", "REFRESH"));
        }, "ask the receiver for a fresh fix");
    }
}
