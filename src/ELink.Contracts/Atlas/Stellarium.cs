using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Atlas;

/// <summary>The Stellarium bridge: ELink acts as a Stellarium telescope (Telescope Control plugin, "external software" at
/// <see cref="TelescopePort"/>) for one Pointer, so Stellarium shows where it points and its slew command drives it; and it talks
/// to Stellarium's Remote Control plugin to show targets there and to read what is selected.</summary>
public class StellariumState : IBinaryConvertible
{
    public BinaryConvertibleInt32 TelescopePort { get; set; } = 0;
    public BinaryConvertibleInt32 TelescopeClients { get; set; } = 0;
    public BinaryConvertibleString PointerId { get; set; } = "";
    public BinaryConvertibleString RemoteUrl { get; set; } = "";
    public BinaryConvertibleBool RemoteReachable { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "StellariumState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static StellariumState()
    {
        d.RegisterField("TelescopePort", (StellariumState x) => x.TelescopePort).Description("TCP port Stellarium's Telescope Control connects to (\"external software\", J2000)");
        d.RegisterField("TelescopeClients", (StellariumState x) => x.TelescopeClients).Description("Stellarium instances connected as telescope clients");
        d.RegisterField("PointerId", (StellariumState x) => x.PointerId).Description("the Pointer (mount or smart scope) Stellarium shows and slews");
        d.RegisterField("RemoteUrl", (StellariumState x) => x.RemoteUrl).Description("Stellarium Remote Control address");
        d.RegisterField("RemoteReachable", (StellariumState x) => x.RemoteReachable);
        d.RegisterField("Message", (StellariumState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class StellariumIds
{
    public const string Root = "ELink.Stellarium";
    public const string State = Root + ".State";
    public const string GetState = Root + ".GetState";
    /// <summary>BinaryConvertibleString (a Pointer id) in, CommandResult out: which pointer Stellarium shows and slews.</summary>
    public const string BindPointer = Root + ".BindPointer";
    /// <summary>SkyTarget in, CommandResult out: centre Stellarium's view on a position.</summary>
    public const string Show = Root + ".Show";
    /// <summary>Void in, AtlasHit out (empty Label when nothing is selected): what is selected in Stellarium now.</summary>
    public const string GetSelection = Root + ".GetSelection";
    /// <summary>Event: AtlasHit, fired when the selection in Stellarium changes.</summary>
    public const string Selected = Root + ".Selected";
}
