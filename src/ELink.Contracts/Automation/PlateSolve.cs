using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>Solve an image: either one passed in, or a fresh shot taken with a Shooter.</summary>
public class SolveRequest : IBinaryConvertible
{
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 2.0;
    public RawBytes Image { get; set; } = new();
    public BinaryConvertibleDouble HintRaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble HintDecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble HintRadiusDegrees { get; set; } = 10.0;
    public BinaryConvertibleDouble ScaleLowArcsecPerPixel { get; set; } = 0.0;
    public BinaryConvertibleDouble ScaleHighArcsecPerPixel { get; set; } = 0.0;
    public BinaryConvertibleDouble TimeoutSeconds { get; set; } = 60.0;

    public override string Name => "SolveRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SolveRequest()
    {
        d.RegisterField("ShooterId", (SolveRequest x) => x.ShooterId).Description("take a shot with this Shooter and solve it; empty = solve Image");
        d.RegisterField("ExposureSeconds", (SolveRequest x) => x.ExposureSeconds).Description("for the shot");
        d.RegisterField("Image", (SolveRequest x) => x.Image).Description("a FITS file, when ShooterId is empty");
        d.RegisterField("HintRaHours", (SolveRequest x) => x.HintRaHours).Description("roughly where it is (J2000); NaN = search the whole sky");
        d.RegisterField("HintDecDegrees", (SolveRequest x) => x.HintDecDegrees);
        d.RegisterField("HintRadiusDegrees", (SolveRequest x) => x.HintRadiusDegrees).Description("how far from the hint to search");
        d.RegisterField("ScaleLowArcsecPerPixel", (SolveRequest x) => x.ScaleLowArcsecPerPixel).Description("0 = unknown");
        d.RegisterField("ScaleHighArcsecPerPixel", (SolveRequest x) => x.ScaleHighArcsecPerPixel).Description("0 = unknown");
        d.RegisterField("TimeoutSeconds", (SolveRequest x) => x.TimeoutSeconds);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class SolveResult : IBinaryConvertible
{
    public BinaryConvertibleBool Solved { get; set; } = false;
    public BinaryConvertibleDouble RaHours { get; set; } = double.NaN;
    public BinaryConvertibleDouble DecDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble PositionAngle { get; set; } = double.NaN;
    public BinaryConvertibleDouble PixelScale { get; set; } = double.NaN;
    public BinaryConvertibleDouble FieldWidthDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble FieldHeightDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble Seconds { get; set; } = 0.0;
    public BinaryConvertibleString ShooterId { get; set; } = "";
    public BinaryConvertibleString Message { get; set; } = "";

    public override string Name => "SolveResult";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static SolveResult()
    {
        d.RegisterField("Solved", (SolveResult x) => x.Solved);
        d.RegisterField("RaHours", (SolveResult x) => x.RaHours).Description("centre of the image, J2000");
        d.RegisterField("DecDegrees", (SolveResult x) => x.DecDegrees).Description("centre of the image, J2000");
        d.RegisterField("PositionAngle", (SolveResult x) => x.PositionAngle).Description("where the image's up points, degrees east of north");
        d.RegisterField("PixelScale", (SolveResult x) => x.PixelScale).Description("arcseconds per pixel");
        d.RegisterField("FieldWidthDegrees", (SolveResult x) => x.FieldWidthDegrees);
        d.RegisterField("FieldHeightDegrees", (SolveResult x) => x.FieldHeightDegrees);
        d.RegisterField("Seconds", (SolveResult x) => x.Seconds).Description("how long solving took");
        d.RegisterField("ShooterId", (SolveResult x) => x.ShooterId);
        d.RegisterField("Message", (SolveResult x) => x.Message).Description("why it did not solve, empty on success");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class SolveIds
{
    public const string Root = "ELink.Automation.Solve";
    /// <summary>SolveRequest in, SolveResult out.</summary>
    public const string Solve = Root + ".Solve";
    /// <summary>Event: SolveResult, after every solve (successful or not).</summary>
    public const string Solved = Root + ".Solved";
}
