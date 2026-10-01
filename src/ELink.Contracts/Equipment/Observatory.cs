using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Dome (and its shutter). Commands under ELink.Dome.&lt;Id&gt;.: Connect(bool), GotoAzimuth(double degrees),
/// Shutter(bool open), Park(bool), Abort(Void), GetState(Void).</summary>
public class DomeState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleDouble AzimuthDegrees { get; set; } = 0.0;
    public BinaryConvertibleBool Moving { get; set; } = false;
    public BinaryConvertibleString Shutter { get; set; } = "Unknown";
    public BinaryConvertibleBool Parked { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "DomeState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static DomeState()
    {
        d.RegisterField("Connected", (DomeState x) => x.Connected);
        d.RegisterField("AzimuthDegrees", (DomeState x) => x.AzimuthDegrees).Range(0, 360);
        d.RegisterField("Moving", (DomeState x) => x.Moving);
        d.RegisterField("Shutter", (DomeState x) => x.Shutter).Description("Open | Closed | Opening | Closing | Unknown");
        d.RegisterField("Parked", (DomeState x) => x.Parked);
        d.RegisterField("Message", (DomeState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class WeatherParameter : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble Value { get; set; } = 0.0;
    public BinaryConvertibleString Status { get; set; } = "Ok";

    public override string Name => "WeatherParameter";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static WeatherParameter()
    {
        d.RegisterField("Id", (WeatherParameter x) => x.Id).Description("e.g. WEATHER_TEMPERATURE");
        d.RegisterField("Label", (WeatherParameter x) => x.Label);
        d.RegisterField("Value", (WeatherParameter x) => x.Value);
        d.RegisterField("Status", (WeatherParameter x) => x.Status).Description("Ok | Warning | Alert");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A weather station with a safety verdict. Commands under ELink.Weather.&lt;Id&gt;.: Connect(bool), Refresh(Void), GetState(Void).</summary>
public class WeatherState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleString Safety { get; set; } = "Unknown";
    public BinaryConvertibleBool Safe { get; set; } = false;
    public BinaryConvertibleCollection<WeatherParameter> Parameters { get; set; } = new();

    public override string Name => "WeatherState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static WeatherState()
    {
        d.RegisterField("Connected", (WeatherState x) => x.Connected);
        d.RegisterField("Safety", (WeatherState x) => x.Safety).Description("Safe | Warning | Unsafe | Unknown");
        d.RegisterField("Safe", (WeatherState x) => x.Safe).Description("true only when the station says it is safe to observe");
        d.RegisterField("Parameters", (WeatherState x) => x.Parameters, maxCount: 64);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A GPS receiver (position and time). Commands under ELink.Gps.&lt;Id&gt;.: Connect(bool), Refresh(Void), GetState(Void).</summary>
public class GpsState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleBool HasFix { get; set; } = false;
    public BinaryConvertibleDouble LatitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble LongitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble ElevationMeters { get; set; } = double.NaN;
    public BinaryConvertibleString TimeUtc { get; set; } = "";

    public override string Name => "GpsState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GpsState()
    {
        d.RegisterField("Connected", (GpsState x) => x.Connected);
        d.RegisterField("HasFix", (GpsState x) => x.HasFix);
        d.RegisterField("LatitudeDegrees", (GpsState x) => x.LatitudeDegrees).Description("north positive");
        d.RegisterField("LongitudeDegrees", (GpsState x) => x.LongitudeDegrees).Description("east positive, -180..180");
        d.RegisterField("ElevationMeters", (GpsState x) => x.ElevationMeters);
        d.RegisterField("TimeUtc", (GpsState x) => x.TimeUtc).Description("ISO 8601, as the receiver reports it");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
