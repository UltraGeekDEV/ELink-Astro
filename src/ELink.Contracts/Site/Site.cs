using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Site;

/// <summary>A point of the local horizon (trees, buildings): the sky is blocked below Altitude at this azimuth.</summary>
public class HorizonPoint : IBinaryConvertible
{
    public BinaryConvertibleDouble AzimuthDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble AltitudeDegrees { get; set; } = 0.0;

    public override string Name => "HorizonPoint";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static HorizonPoint()
    {
        d.RegisterField("AzimuthDegrees", (HorizonPoint x) => x.AzimuthDegrees).Description("from north through east");
        d.RegisterField("AltitudeDegrees", (HorizonPoint x) => x.AltitudeDegrees);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SiteConfig : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "Home";
    public BinaryConvertibleDouble LatitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble LongitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble ElevationMeters { get; set; } = 0.0;
    public BinaryConvertibleString GpsId { get; set; } = "";
    public BinaryConvertibleBool PushToMounts { get; set; } = true;
    public BinaryConvertibleDouble MinAltitudeDegrees { get; set; } = 0.0;
    public BinaryConvertibleCollection<HorizonPoint> Horizon { get; set; } = new();

    public override string Name => "SiteConfig";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SiteConfig()
    {
        d.RegisterField("Label", (SiteConfig x) => x.Label);
        d.RegisterField("LatitudeDegrees", (SiteConfig x) => x.LatitudeDegrees).Description("north positive; NaN = unknown");
        d.RegisterField("LongitudeDegrees", (SiteConfig x) => x.LongitudeDegrees).Description("east positive, -180..180");
        d.RegisterField("ElevationMeters", (SiteConfig x) => x.ElevationMeters);
        d.RegisterField("GpsId", (SiteConfig x) => x.GpsId).Description("Gps device to take the position (and check the clock) from; empty = the numbers above");
        d.RegisterField("PushToMounts", (SiteConfig x) => x.PushToMounts).Description("give every mount on the mesh this location and the time, as Ekos does on connect");
        d.RegisterField("MinAltitudeDegrees", (SiteConfig x) => x.MinAltitudeDegrees).Description("flat horizon: nothing below this counts as observable");
        d.RegisterField("Horizon", (SiteConfig x) => x.Horizon, maxCount: 360).Description("local horizon profile; above MinAltitude where it is higher");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SiteState : IBinaryConvertible
{
    public SiteConfig Config { get; set; } = new();
    public BinaryConvertibleBool Known { get; set; } = false;
    public BinaryConvertibleString Source { get; set; } = "";
    public BinaryConvertibleString UtcNow { get; set; } = "";
    public BinaryConvertibleDouble LocalSiderealHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble ClockOffsetSeconds { get; set; } = double.NaN;
    public BinaryConvertibleDouble SunAltitude { get; set; } = double.NaN;
    public BinaryConvertibleString Sky { get; set; } = "";
    public BinaryConvertibleString DuskUtc { get; set; } = "";
    public BinaryConvertibleString DawnUtc { get; set; } = "";
    public BinaryConvertibleDouble MoonRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble MoonDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble MoonAltitude { get; set; } = double.NaN;
    public BinaryConvertibleDouble MoonIllumination { get; set; } = double.NaN;
    public BinaryConvertibleString MoonRiseUtc { get; set; } = "";
    public BinaryConvertibleString MoonSetUtc { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "SiteState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SiteState()
    {
        d.RegisterField("Config", (SiteState x) => x.Config);
        d.RegisterField("Known", (SiteState x) => x.Known).Description("the location is set (or the GPS has a fix)");
        d.RegisterField("Source", (SiteState x) => x.Source).Description("Manual | GPS <id> | (none)");
        d.RegisterField("UtcNow", (SiteState x) => x.UtcNow).Description("when this state was made, ISO 8601");
        d.RegisterField("LocalSiderealHours", (SiteState x) => x.LocalSiderealHours);
        d.RegisterField("ClockOffsetSeconds", (SiteState x) => x.ClockOffsetSeconds).Description("GPS time minus this computer's clock; NaN without a GPS");
        d.RegisterField("SunAltitude", (SiteState x) => x.SunAltitude);
        d.RegisterField("Sky", (SiteState x) => x.Sky).Description("Day | CivilTwilight | NauticalTwilight | AstronomicalTwilight | Night");
        d.RegisterField("DuskUtc", (SiteState x) => x.DuskUtc).Description("next astronomical dusk (Sun 18 deg down), empty if none within a day");
        d.RegisterField("DawnUtc", (SiteState x) => x.DawnUtc).Description("next astronomical dawn");
        d.RegisterField("MoonRaHours", (SiteState x) => x.MoonRaHours).Description("topocentric, J2000");
        d.RegisterField("MoonDecDegrees", (SiteState x) => x.MoonDecDegrees);
        d.RegisterField("MoonAltitude", (SiteState x) => x.MoonAltitude);
        d.RegisterField("MoonIllumination", (SiteState x) => x.MoonIllumination).Description("0..1");
        d.RegisterField("MoonRiseUtc", (SiteState x) => x.MoonRiseUtc).Description("next moonrise");
        d.RegisterField("MoonSetUtc", (SiteState x) => x.MoonSetUtc).Description("next moonset");
        d.RegisterField("Message", (SiteState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ObservabilityRequest : IBinaryConvertible
{
    public SkyTarget Target { get; set; } = new();
    public BinaryConvertibleDouble MinAltitudeDegrees { get; set; } = double.NaN;
    public BinaryConvertibleString FromUtc { get; set; } = "";
    public BinaryConvertibleDouble Hours { get; set; } = 24.0;

    public override string Name => "ObservabilityRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ObservabilityRequest()
    {
        d.RegisterField("Target", (ObservabilityRequest x) => x.Target);
        d.RegisterField("MinAltitudeDegrees", (ObservabilityRequest x) => x.MinAltitudeDegrees).Description("NaN = the site's horizon (flat minimum and profile)");
        d.RegisterField("FromUtc", (ObservabilityRequest x) => x.FromUtc).Description("ISO 8601; empty = now");
        d.RegisterField("Hours", (ObservabilityRequest x) => x.Hours).Range(0.1m, 240m);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ObservabilityResult : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleDouble Altitude { get; set; } = double.NaN;
    public BinaryConvertibleDouble Azimuth { get; set; } = double.NaN;
    public BinaryConvertibleDouble HourAngleHours { get; set; } = double.NaN;
    public BinaryConvertibleBool AboveHorizon { get; set; } = false;
    public BinaryConvertibleString RiseUtc { get; set; } = "";
    public BinaryConvertibleString SetUtc { get; set; } = "";
    public BinaryConvertibleString TransitUtc { get; set; } = "";
    public BinaryConvertibleDouble TransitAltitude { get; set; } = double.NaN;
    public BinaryConvertibleDouble DarkHoursVisible { get; set; } = 0.0;
    public BinaryConvertibleDouble MoonSeparationDegrees { get; set; } = double.NaN;

    public override string Name => "ObservabilityResult";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ObservabilityResult()
    {
        d.RegisterField("Ok", (ObservabilityResult x) => x.Ok);
        d.RegisterField("Message", (ObservabilityResult x) => x.Message);
        d.RegisterField("Altitude", (ObservabilityResult x) => x.Altitude).Description("at FromUtc, geometric");
        d.RegisterField("Azimuth", (ObservabilityResult x) => x.Azimuth);
        d.RegisterField("HourAngleHours", (ObservabilityResult x) => x.HourAngleHours).Description("-12..12, negative = east of the meridian");
        d.RegisterField("AboveHorizon", (ObservabilityResult x) => x.AboveHorizon).Description("above the minimum altitude and the horizon profile now");
        d.RegisterField("RiseUtc", (ObservabilityResult x) => x.RiseUtc).Description("next time it climbs above the minimum altitude, empty if not within Hours");
        d.RegisterField("SetUtc", (ObservabilityResult x) => x.SetUtc);
        d.RegisterField("TransitUtc", (ObservabilityResult x) => x.TransitUtc).Description("next upper culmination");
        d.RegisterField("TransitAltitude", (ObservabilityResult x) => x.TransitAltitude);
        d.RegisterField("DarkHoursVisible", (ObservabilityResult x) => x.DarkHoursVisible).Description("hours within the window that are astronomically dark with the target above the horizon");
        d.RegisterField("MoonSeparationDegrees", (ObservabilityResult x) => x.MoonSeparationDegrees);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SkyBody : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble Altitude { get; set; } = double.NaN;
    public BinaryConvertibleDouble Azimuth { get; set; } = double.NaN;
    public BinaryConvertibleDouble DistanceAu { get; set; } = 0.0;
    public BinaryConvertibleDouble Illumination { get; set; } = double.NaN;
    public BinaryConvertibleDouble ElongationDegrees { get; set; } = 0.0;

    public override string Name => "SkyBody";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SkyBody()
    {
        d.RegisterField("Label", (SkyBody x) => x.Label).Description("Sun, Moon, Mercury ... Neptune");
        d.RegisterField("RaHours", (SkyBody x) => x.RaHours).Description("J2000, seen from the site (topocentric) when it is known");
        d.RegisterField("DecDegrees", (SkyBody x) => x.DecDegrees);
        d.RegisterField("Altitude", (SkyBody x) => x.Altitude).Description("NaN when the site is unknown");
        d.RegisterField("Azimuth", (SkyBody x) => x.Azimuth);
        d.RegisterField("DistanceAu", (SkyBody x) => x.DistanceAu);
        d.RegisterField("Illumination", (SkyBody x) => x.Illumination).Description("lit fraction 0..1; NaN for the Sun");
        d.RegisterField("ElongationDegrees", (SkyBody x) => x.ElongationDegrees).Description("angle from the Sun");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SkyBodies : IBinaryConvertible
{
    public BinaryConvertibleString UtcAt { get; set; } = "";
    public BinaryConvertibleCollection<SkyBody> Bodies { get; set; } = new();

    public override string Name => "SkyBodies";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SkyBodies()
    {
        d.RegisterField("UtcAt", (SkyBodies x) => x.UtcAt);
        d.RegisterField("Bodies", (SkyBodies x) => x.Bodies, maxCount: 16);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Location pushed to a mount (INDI GEOGRAPHIC_COORD).</summary>
public class GeoLocation : IBinaryConvertible
{
    public BinaryConvertibleDouble LatitudeDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble LongitudeDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble ElevationMeters { get; set; } = 0.0;

    public override string Name => "GeoLocation";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static GeoLocation()
    {
        d.RegisterField("LatitudeDegrees", (GeoLocation x) => x.LatitudeDegrees);
        d.RegisterField("LongitudeDegrees", (GeoLocation x) => x.LongitudeDegrees).Description("east positive, -180..180");
        d.RegisterField("ElevationMeters", (GeoLocation x) => x.ElevationMeters);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A site is a shared physical fact scopes may consult (where, when, how dark); it never drives them.
/// Several sites can exist on one mesh (remote observatories), each under its own id.</summary>
public static class SiteIds
{
    public const string Default = "home";
    public static string Root(string id) => "ELink.Site." + id;
    /// <summary>SiteConfig in, CommandResult out.</summary>
    public static string Configure(string id) => Root(id) + ".Configure";
    public static string State(string id) => Root(id) + ".State";
    public static string GetState(string id) => Root(id) + ".GetState";
    /// <summary>ObservabilityRequest in, ObservabilityResult out.</summary>
    public static string Observability(string id) => Root(id) + ".Observability";
    /// <summary>Void in, SkyBodies out: Sun, Moon and planets now, as seen from the site.</summary>
    public static string Bodies(string id) => Root(id) + ".Bodies";
    /// <summary>Mount commands the site uses to hand over location and time.</summary>
    public const string MountSetLocation = "SetLocation", MountSetTime = "SetTime";
}
