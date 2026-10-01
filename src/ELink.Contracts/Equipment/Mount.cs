using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Equipment;

/// <summary>Where a mount is and what it is doing. Commands (functions under ELink.Mount.&lt;Id&gt;.):
/// Connect(bool), Goto(<see cref="SkyTarget"/>), Sync(<see cref="SkyTarget"/>), Abort(Void), Park(bool), SetTracking(bool).</summary>
public class MountState : IBinaryConvertible
{
    public BinaryConvertibleBool Connected { get; set; } = false;
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleString Epoch { get; set; } = "JNow";
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleBool Tracking { get; set; } = false;
    public BinaryConvertibleBool Parked { get; set; } = false;
    public BinaryConvertibleString PierSide { get; set; } = "Unknown";
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "MountState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MountState()
    {
        d.RegisterField("Connected", (MountState x) => x.Connected);
        d.RegisterField("RaHours", (MountState x) => x.RaHours).Description("right ascension in hours, in the given Epoch").Range(0, 24);
        d.RegisterField("DecDegrees", (MountState x) => x.DecDegrees).Description("declination in degrees, in the given Epoch").Range(-90, 90);
        d.RegisterField("Epoch", (MountState x) => x.Epoch).Description("JNow | J2000: the equinox of the coordinates above");
        d.RegisterField("Phase", (MountState x) => x.Phase).Description("Disconnected | Idle | Slewing | Tracking | Parking | Parked | Error");
        d.RegisterField("Tracking", (MountState x) => x.Tracking);
        d.RegisterField("Parked", (MountState x) => x.Parked);
        d.RegisterField("PierSide", (MountState x) => x.PierSide).Description("East | West | Unknown");
        d.RegisterField("Message", (MountState x) => x.Message).Description("last problem or note, empty if none");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A point on the sky.</summary>
public class SkyTarget : IBinaryConvertible
{
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleString Epoch { get; set; } = "J2000";

    public override string Name => "SkyTarget";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SkyTarget()
    {
        d.RegisterField("RaHours", (SkyTarget x) => x.RaHours).Range(0, 24);
        d.RegisterField("DecDegrees", (SkyTarget x) => x.DecDegrees).Range(-90, 90);
        d.RegisterField("Epoch", (SkyTarget x) => x.Epoch).Description("JNow | J2000");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
