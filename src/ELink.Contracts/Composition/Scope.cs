using ELink.Contracts.Equipment;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Composition;

/// <summary>A shooter belonging to a scope, with where it looks relative to the scope's pointing axis.</summary>
public class ScopeShooterRef : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleDouble OffsetEastArcmin { get; set; } = 0.0;
    public BinaryConvertibleDouble OffsetNorthArcmin { get; set; } = 0.0;

    public override string Name => "ScopeShooterRef";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScopeShooterRef()
    {
        d.RegisterField("Id", (ScopeShooterRef x) => x.Id).Description("a Shooter id (a camera shooter or another scope)");
        d.RegisterField("OffsetEastArcmin", (ScopeShooterRef x) => x.OffsetEastArcmin).Description("how far east of the pointing axis this shooter's centre is");
        d.RegisterField("OffsetNorthArcmin", (ScopeShooterRef x) => x.OffsetNorthArcmin).Description("how far north of the pointing axis this shooter's centre is");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A smart scope: point it, shoot with it. Any number of pointers (all slewed to the same place) and any number
/// of shooters; a scope is itself a Pointer and a Shooter under the same id, so scopes compose into scopes.</summary>
public class ScopeDefinition : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString DisplayName { get; set; } = "";
    public BinaryConvertibleCollection<BinaryConvertibleString> Pointers { get; set; } = new();
    public BinaryConvertibleCollection<ScopeShooterRef> Shooters { get; set; } = new();

    public override string Name => "ScopeDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScopeDefinition()
    {
        d.RegisterField("Id", (ScopeDefinition x) => x.Id).Description("scope id: ELink.Scope.<Id>.*, plus Pointer.<Id> and Shooter.<Id> so it can be part of another scope");
        d.RegisterField("DisplayName", (ScopeDefinition x) => x.DisplayName);
        d.RegisterField("Pointers", (ScopeDefinition x) => x.Pointers, maxCount: 64).Description("Pointer ids; the first is the reference, all are sent to the same target");
        d.RegisterField("Shooters", (ScopeDefinition x) => x.Shooters, maxCount: 64);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ObserveRequest : IBinaryConvertible
{
    public SkyTarget Target { get; set; } = new();
    public ShooterExposure Exposure { get; set; } = new();
    public BinaryConvertibleInt32 Count { get; set; } = 1;
    public BinaryConvertibleDouble SlewTimeoutSeconds { get; set; } = 300.0;

    public override string Name => "ObserveRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ObserveRequest()
    {
        d.RegisterField("Target", (ObserveRequest x) => x.Target);
        d.RegisterField("Exposure", (ObserveRequest x) => x.Exposure);
        d.RegisterField("Count", (ObserveRequest x) => x.Count).Range(1, 100000);
        d.RegisterField("SlewTimeoutSeconds", (ObserveRequest x) => x.SlewTimeoutSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Commands under ELink.Scope.&lt;Id&gt;.: Observe(<see cref="ObserveRequest"/>), Goto(SkyTarget), Expose(ShooterExposure), Abort(Void), GetState(Void).</summary>
public class ScopeState : IBinaryConvertible
{
    public BinaryConvertibleString Phase { get; set; } = "Idle";
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 ShotsDone { get; set; } = 0;
    public BinaryConvertibleInt32 ShotsPlanned { get; set; } = 0;
    public BinaryConvertibleBool Observing { get; set; } = false;

    public override string Name => "ScopeState";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScopeState()
    {
        d.RegisterField("Phase", (ScopeState x) => x.Phase).Description("Idle | Pointing | OnTarget | Exposing | Error");
        d.RegisterField("Message", (ScopeState x) => x.Message);
        d.RegisterField("ShotsDone", (ScopeState x) => x.ShotsDone).Description("exposure rounds finished in the current/last Observe");
        d.RegisterField("ShotsPlanned", (ScopeState x) => x.ShotsPlanned);
        d.RegisterField("Observing", (ScopeState x) => x.Observing).Description("an Observe run is active");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ScopeIds
{
    public const string Kind = "Scope";
    public static string State(string id) => EquipmentIds.State(Kind, id);
    public static string GetState(string id) => EquipmentIds.GetState(Kind, id);
    public static string Command(string id, string command) => EquipmentIds.Command(Kind, id, command);

    // Functions of a composition host (every host answers a List call; a Define reaches whichever hosts exist).
    /// <summary>ScopeDefinition in, CommandResult out. Creates or replaces a scope.</summary>
    public const string Define = "ELink.Compose.Define";
    /// <summary>MountPointerDefinition in, CommandResult out. Makes a Mount usable as a Pointer.</summary>
    public const string DefineMountPointer = "ELink.Compose.DefineMountPointer";
    /// <summary>CameraShooterDefinition in, CommandResult out. Makes a Camera (+ filter wheel) usable as a Shooter.</summary>
    public const string DefineCameraShooter = "ELink.Compose.DefineCameraShooter";
    /// <summary>BinaryConvertibleString "Scope:id", "Pointer:id" or "Shooter:id" in, CommandResult out.</summary>
    public const string Remove = "ELink.Compose.Remove";
    /// <summary>Void in, <see cref="CompositionSnapshot"/> out (one per composition host).</summary>
    public const string Snapshot = "ELink.Compose.Snapshot";
    /// <summary>Event: <see cref="CompositionSnapshot"/>, fired whenever a host's composition changes.</summary>
    public const string Changed = "ELink.Compose.Changed";
}

/// <summary>Use a Mount as a Pointer named <see cref="Id"/>.</summary>
public class MountPointerDefinition : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString MountId { get; set; } = "";

    public override string Name => "MountPointerDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static MountPointerDefinition()
    {
        d.RegisterField("Id", (MountPointerDefinition x) => x.Id).Description("the new Pointer id");
        d.RegisterField("MountId", (MountPointerDefinition x) => x.MountId).Description("a Mount id from ELink.Devices.List");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Use a Camera (and optionally a FilterWheel) as a Shooter named <see cref="Id"/>.</summary>
public class CameraShooterDefinition : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString CameraId { get; set; } = "";
    public BinaryConvertibleString FilterWheelId { get; set; } = "";

    public override string Name => "CameraShooterDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CameraShooterDefinition()
    {
        d.RegisterField("Id", (CameraShooterDefinition x) => x.Id).Description("the new Shooter id");
        d.RegisterField("CameraId", (CameraShooterDefinition x) => x.CameraId);
        d.RegisterField("FilterWheelId", (CameraShooterDefinition x) => x.FilterWheelId).Description("empty = no filter wheel");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Everything one composition host currently composes.</summary>
public class CompositionSnapshot : IBinaryConvertible
{
    public BinaryConvertibleCollection<MountPointerDefinition> MountPointers { get; set; } = new();
    public BinaryConvertibleCollection<CameraShooterDefinition> CameraShooters { get; set; } = new();
    public BinaryConvertibleCollection<ScopeDefinition> Scopes { get; set; } = new();

    public override string Name => "CompositionSnapshot";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CompositionSnapshot()
    {
        d.RegisterField("MountPointers", (CompositionSnapshot x) => x.MountPointers);
        d.RegisterField("CameraShooters", (CompositionSnapshot x) => x.CameraShooters);
        d.RegisterField("Scopes", (CompositionSnapshot x) => x.Scopes);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
