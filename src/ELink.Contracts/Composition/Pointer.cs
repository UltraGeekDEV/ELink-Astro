using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Composition;

/// <summary>Something that can be pointed at the sky: a mount, a mount plus dome, a group of mounts, a smart scope.
/// Commands under ELink.Pointer.&lt;Id&gt;.: Goto(<see cref="SkyTarget"/>), Abort(Void), GetState(Void).</summary>
public class PointerState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Disconnected";
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleBool OnTarget { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "PointerState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static PointerState()
    {
        d.RegisterField("Phase", (PointerState x) => x.Phase).Description("Disconnected | Idle | Slewing | OnTarget | Parked | Error");
        d.RegisterField("RaHours", (PointerState x) => x.RaHours).Description("where it points now, J2000");
        d.RegisterField("DecDegrees", (PointerState x) => x.DecDegrees).Description("where it points now, J2000");
        d.RegisterField("OnTarget", (PointerState x) => x.OnTarget).Description("settled on the last commanded target");
        d.RegisterField("Message", (PointerState x) => x.Message);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class PointerIds
{
    public const string Kind = "Pointer";
    public static string State(string id) => EquipmentIds.State(Kind, id);
    public static string GetState(string id) => EquipmentIds.GetState(Kind, id);
    public static string Goto(string id) => EquipmentIds.Command(Kind, id, "Goto");
    public static string Abort(string id) => EquipmentIds.Command(Kind, id, "Abort");
}
