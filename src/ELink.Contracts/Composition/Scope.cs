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
    public BinaryConvertibleString GuiderId { get; set; } = "";
    public BinaryConvertibleString GuideShooterId { get; set; } = "";
    public BinaryConvertibleString GuideOutput { get; set; } = "Pulse";
    public BinaryConvertibleString GuidePortId { get; set; } = "";
    public BinaryConvertibleString GuideTargetId { get; set; } = "";
    public BinaryConvertibleDouble GuideExposureSeconds { get; set; } = 2.0;
    public BinaryConvertibleBool CenterAfterSlew { get; set; } = false;
    public BinaryConvertibleString CenterShooterId { get; set; } = "";
    public BinaryConvertibleDouble CenterToleranceArcmin { get; set; } = 1.0;
    public BinaryConvertibleDouble CenterExposureSeconds { get; set; } = 3.0;
    public BinaryConvertibleInt32 CenterMaxTries { get; set; } = 4;
    public BinaryConvertibleBool GradeFrames { get; set; } = true;
    public BinaryConvertibleBool FocusOnStart { get; set; } = false;
    public BinaryConvertibleDouble RefocusEveryMinutes { get; set; } = 0.0;
    public BinaryConvertibleDouble RefocusTemperatureDelta { get; set; } = 0.0;
    public BinaryConvertibleBool RefocusOnFilterChange { get; set; } = false;
    public BinaryConvertibleDouble RefocusHfrIncreasePercent { get; set; } = 0.0;
    public BinaryConvertibleDouble FocusExposureSeconds { get; set; } = 3.0;
    public BinaryConvertibleInt32 FocusStepSize { get; set; } = 3000;
    public BinaryConvertibleInt32 FocusSamples { get; set; } = 7;
    public BinaryConvertibleBool MeridianFlip { get; set; } = true;
    public BinaryConvertibleDouble FlipAfterHours { get; set; } = 0.1;
    public BinaryConvertibleString SiteId { get; set; } = "home";
    public BinaryConvertibleInt32 DitherEvery { get; set; } = 0;
    public BinaryConvertibleDouble DitherPixels { get; set; } = 5.0;
    public BinaryConvertibleDouble SettlePixels { get; set; } = 1.5;
    public BinaryConvertibleDouble SettleSeconds { get; set; } = 10.0;
    public BinaryConvertibleDouble SettleTimeoutSeconds { get; set; } = 120.0;

    public override string Name => "ScopeDefinition";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ScopeDefinition()
    {
        d.RegisterField("Id", (ScopeDefinition x) => x.Id).Description("scope id: ELink.Scope.<Id>.*, plus Pointer.<Id> and Shooter.<Id> so it can be part of another scope");
        d.RegisterField("DisplayName", (ScopeDefinition x) => x.DisplayName);
        d.RegisterField("Pointers", (ScopeDefinition x) => x.Pointers, maxCount: 64).Description("Pointer ids; the first is the reference, all are sent to the same target");
        d.RegisterField("Shooters", (ScopeDefinition x) => x.Shooters, maxCount: 64);
        d.RegisterField("GuiderId", (ScopeDefinition x) => x.GuiderId).Description("the scope's own guider: it guides between slews and dithers between frames by itself; empty = unguided");
        d.RegisterField("GuideShooterId", (ScopeDefinition x) => x.GuideShooterId).Description("guide with this: a whole train (a guide scope), a train's guide camera (<train>-guide, an OAG) or any Shooter; the scope then owns a guider of its own. Ignored when GuiderId is set");
        d.RegisterField("GuideOutput", (ScopeDefinition x) => x.GuideOutput).Description("Pulse | Correction");
        d.RegisterField("GuidePortId", (ScopeDefinition x) => x.GuidePortId).Description("GuidePort for Pulse; empty = the first pointer's mount");
        d.RegisterField("GuideTargetId", (ScopeDefinition x) => x.GuideTargetId).Description("GuideTarget for Correction");
        d.RegisterField("GuideExposureSeconds", (ScopeDefinition x) => x.GuideExposureSeconds);
        d.RegisterField("CenterAfterSlew", (ScopeDefinition x) => x.CenterAfterSlew).Description("after each of its slews: plate solve, re-aim by the error until within tolerance, learn the pointing correction for later slews");
        d.RegisterField("CenterShooterId", (ScopeDefinition x) => x.CenterShooterId).Description("the shooter to solve with; empty = the primary (first) shooter");
        d.RegisterField("CenterToleranceArcmin", (ScopeDefinition x) => x.CenterToleranceArcmin);
        d.RegisterField("CenterExposureSeconds", (ScopeDefinition x) => x.CenterExposureSeconds);
        d.RegisterField("CenterMaxTries", (ScopeDefinition x) => x.CenterMaxTries);
        d.RegisterField("GradeFrames", (ScopeDefinition x) => x.GradeFrames).Description("measure every Light frame and mark the bad ones (clouds, trailing, soft, bright sky) Rejected: they do not count toward an image's depth and are not stacked");
        d.RegisterField("FocusOnStart", (ScopeDefinition x) => x.FocusOnStart).Description("autofocus every train with a focuser before the scope's first exposure");
        d.RegisterField("RefocusEveryMinutes", (ScopeDefinition x) => x.RefocusEveryMinutes).Description("0 = never by time");
        d.RegisterField("RefocusTemperatureDelta", (ScopeDefinition x) => x.RefocusTemperatureDelta).Description("refocus when the focuser's temperature has moved this many °C since the last focus; 0 = never");
        d.RegisterField("RefocusOnFilterChange", (ScopeDefinition x) => x.RefocusOnFilterChange);
        d.RegisterField("RefocusHfrIncreasePercent", (ScopeDefinition x) => x.RefocusHfrIncreasePercent).Description("refocus when stars have grown this much since the last focus (median of the last 3 frames); 0 = never");
        d.RegisterField("FocusExposureSeconds", (ScopeDefinition x) => x.FocusExposureSeconds);
        d.RegisterField("FocusStepSize", (ScopeDefinition x) => x.FocusStepSize).Description("focuser steps between samples");
        d.RegisterField("FocusSamples", (ScopeDefinition x) => x.FocusSamples);
        d.RegisterField("MeridianFlip", (ScopeDefinition x) => x.MeridianFlip).Description("flip by itself when the target passes the meridian (German equatorial mounts)");
        d.RegisterField("FlipAfterHours", (ScopeDefinition x) => x.FlipAfterHours).Description("how far past the meridian (hour angle) to flip; never in the middle of an exposure");
        d.RegisterField("SiteId", (ScopeDefinition x) => x.SiteId).Description("the site whose sidereal time gives the hour angle");
        d.RegisterField("DitherEvery", (ScopeDefinition x) => x.DitherEvery).Description("dither after every N exposure rounds; 0 = never");
        d.RegisterField("DitherPixels", (ScopeDefinition x) => x.DitherPixels).Description("dither size, guide camera pixels");
        d.RegisterField("SettlePixels", (ScopeDefinition x) => x.SettlePixels).Description("guiding counts as settled below this error ...");
        d.RegisterField("SettleSeconds", (ScopeDefinition x) => x.SettleSeconds).Description("... held for this long");
        d.RegisterField("SettleTimeoutSeconds", (ScopeDefinition x) => x.SettleTimeoutSeconds).Description("how long to wait for guiding to settle before starting (or carrying on)");
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
    public BinaryConvertibleString ObjectName { get; set; } = "";
    public BinaryConvertibleString PlanId { get; set; } = "";

    public override string Name => "ObserveRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ObserveRequest()
    {
        d.RegisterField("Target", (ObserveRequest x) => x.Target);
        d.RegisterField("Exposure", (ObserveRequest x) => x.Exposure);
        d.RegisterField("Count", (ObserveRequest x) => x.Count).Range(1, 100000);
        d.RegisterField("SlewTimeoutSeconds", (ObserveRequest x) => x.SlewTimeoutSeconds);
        d.RegisterField("ObjectName", (ObserveRequest x) => x.ObjectName).Description("stamped on every frame of this run (FITS OBJECT, file names)");
        d.RegisterField("PlanId", (ObserveRequest x) => x.PlanId).Description("stamped on every frame of this run");
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
        d.RegisterField("Phase", (ScopeState x) => x.Phase).Description("Idle | Pointing | OnTarget | Guiding (starting, settling) | Exposing | Dithering | Centering | Focusing | WaitingForFlip | Flipping | Error");
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
    /// <summary>GuiderDefinition in, CommandResult out.</summary>
    public const string DefineGuider = "ELink.Compose.DefineGuider";
    /// <summary>ImagingTrainDefinition in, CommandResult out.</summary>
    public const string DefineTrain = "ELink.Compose.DefineTrain";
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
    public BinaryConvertibleCollection<GuiderDefinition> Guiders { get; set; } = new();
    public BinaryConvertibleCollection<ImagingTrainDefinition> Trains { get; set; } = new();

    public override string Name => "CompositionSnapshot";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static CompositionSnapshot()
    {
        d.RegisterField("MountPointers", (CompositionSnapshot x) => x.MountPointers);
        d.RegisterField("CameraShooters", (CompositionSnapshot x) => x.CameraShooters);
        d.RegisterField("Scopes", (CompositionSnapshot x) => x.Scopes);
        d.RegisterField("Guiders", (CompositionSnapshot x) => x.Guiders);
        d.RegisterField("Trains", (CompositionSnapshot x) => x.Trains);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}
