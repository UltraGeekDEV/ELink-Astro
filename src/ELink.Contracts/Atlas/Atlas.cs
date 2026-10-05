using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Atlas;

/// <summary>A region of the sky to fetch from the atlas.</summary>
public class AtlasQuery : IBinaryConvertible
{
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble RadiusDegrees { get; set; } = 10.0;
    public BinaryConvertibleDouble StarMagnitudeLimit { get; set; } = 6.5;
    public BinaryConvertibleDouble DsoMagnitudeLimit { get; set; } = 12.0;
    public BinaryConvertibleInt32 MaxStars { get; set; } = 20000;
    public BinaryConvertibleInt32 MaxDsos { get; set; } = 2000;

    public override string Name => "AtlasQuery";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasQuery()
    {
        d.RegisterField("RaHours", (AtlasQuery x) => x.RaHours).Description("centre, J2000");
        d.RegisterField("DecDegrees", (AtlasQuery x) => x.DecDegrees).Description("centre, J2000");
        d.RegisterField("RadiusDegrees", (AtlasQuery x) => x.RadiusDegrees).Range(0.01m, 180m);
        d.RegisterField("StarMagnitudeLimit", (AtlasQuery x) => x.StarMagnitudeLimit).Description("faintest star to return");
        d.RegisterField("DsoMagnitudeLimit", (AtlasQuery x) => x.DsoMagnitudeLimit).Description("faintest deep-sky object to return (objects without a magnitude are returned when large enough)");
        d.RegisterField("MaxStars", (AtlasQuery x) => x.MaxStars).Description("the brightest N are kept");
        d.RegisterField("MaxDsos", (AtlasQuery x) => x.MaxDsos);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class AtlasStar : IBinaryConvertible
{
    public BinaryConvertibleFloat RaHours { get; set; } = 0f;
    public BinaryConvertibleFloat DecDegrees { get; set; } = 0f;
    public BinaryConvertibleFloat Magnitude { get; set; } = 0f;
    public BinaryConvertibleFloat ColorIndex { get; set; } = 0f;
    public BinaryConvertibleString Label { get; set; } = "";

    public override string Name => "AtlasStar";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasStar()
    {
        d.RegisterField("RaHours", (AtlasStar x) => x.RaHours).Description("J2000");
        d.RegisterField("DecDegrees", (AtlasStar x) => x.DecDegrees).Description("J2000");
        d.RegisterField("Magnitude", (AtlasStar x) => x.Magnitude).Description("visual");
        d.RegisterField("ColorIndex", (AtlasStar x) => x.ColorIndex).Description("B-V");
        d.RegisterField("Label", (AtlasStar x) => x.Label).Description("proper or Bayer name, empty for most stars");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class AtlasDso : IBinaryConvertible
{
    public BinaryConvertibleString Id { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "";
    public BinaryConvertibleString CommonName { get; set; } = "";
    public BinaryConvertibleFloat RaHours { get; set; } = 0f;
    public BinaryConvertibleFloat DecDegrees { get; set; } = 0f;
    public BinaryConvertibleFloat Magnitude { get; set; } = float.NaN;
    public BinaryConvertibleFloat MajorArcmin { get; set; } = 0f;
    public BinaryConvertibleFloat MinorArcmin { get; set; } = 0f;
    public BinaryConvertibleFloat PositionAngle { get; set; } = 0f;

    public override string Name => "AtlasDso";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasDso()
    {
        d.RegisterField("Id", (AtlasDso x) => x.Id).Description("e.g. M 31, NGC 7000, IC 434");
        d.RegisterField("Kind", (AtlasDso x) => x.Kind).Description("Galaxy | OpenCluster | GlobularCluster | Nebula | PlanetaryNebula | SupernovaRemnant | DarkNebula | Asterism | GalaxyCluster | MultipleStar | Other");
        d.RegisterField("CommonName", (AtlasDso x) => x.CommonName).Description("e.g. Andromeda Galaxy; also other designations");
        d.RegisterField("RaHours", (AtlasDso x) => x.RaHours).Description("J2000");
        d.RegisterField("DecDegrees", (AtlasDso x) => x.DecDegrees).Description("J2000");
        d.RegisterField("Magnitude", (AtlasDso x) => x.Magnitude).Description("NaN if unknown");
        d.RegisterField("MajorArcmin", (AtlasDso x) => x.MajorArcmin).Description("size along the major axis, 0 if unknown");
        d.RegisterField("MinorArcmin", (AtlasDso x) => x.MinorArcmin);
        d.RegisterField("PositionAngle", (AtlasDso x) => x.PositionAngle).Description("of the major axis, north through east");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>What the atlas has in a region.</summary>
public class AtlasChunk : IBinaryConvertible
{
    public BinaryConvertibleCollection<AtlasStar> Stars { get; set; } = new();
    public BinaryConvertibleCollection<AtlasDso> Dsos { get; set; } = new();
    public BinaryConvertibleBool Truncated { get; set; } = false;

    public override string Name => "AtlasChunk";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasChunk()
    {
        d.RegisterField("Stars", (AtlasChunk x) => x.Stars);
        d.RegisterField("Dsos", (AtlasChunk x) => x.Dsos);
        d.RegisterField("Truncated", (AtlasChunk x) => x.Truncated).Description("more objects matched than the limits allowed");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ConstellationLine : IBinaryConvertible
{
    public BinaryConvertibleFloat Ra1Hours { get; set; } = 0f;
    public BinaryConvertibleFloat Dec1Degrees { get; set; } = 0f;
    public BinaryConvertibleFloat Ra2Hours { get; set; } = 0f;
    public BinaryConvertibleFloat Dec2Degrees { get; set; } = 0f;

    public override string Name => "ConstellationLine";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ConstellationLine()
    {
        d.RegisterField("Ra1Hours", (ConstellationLine x) => x.Ra1Hours);
        d.RegisterField("Dec1Degrees", (ConstellationLine x) => x.Dec1Degrees);
        d.RegisterField("Ra2Hours", (ConstellationLine x) => x.Ra2Hours);
        d.RegisterField("Dec2Degrees", (ConstellationLine x) => x.Dec2Degrees);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ConstellationLabel : IBinaryConvertible
{
    public BinaryConvertibleString Abbreviation { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleFloat RaHours { get; set; } = 0f;
    public BinaryConvertibleFloat DecDegrees { get; set; } = 0f;

    public override string Name => "ConstellationLabel";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ConstellationLabel()
    {
        d.RegisterField("Abbreviation", (ConstellationLabel x) => x.Abbreviation).Description("IAU, e.g. Ori");
        d.RegisterField("Label", (ConstellationLabel x) => x.Label).Description("e.g. Orion");
        d.RegisterField("RaHours", (ConstellationLabel x) => x.RaHours).Description("where to put the label");
        d.RegisterField("DecDegrees", (ConstellationLabel x) => x.DecDegrees);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Constellation figures for the whole sky (small: fetch once).</summary>
public class ConstellationSet : IBinaryConvertible
{
    public BinaryConvertibleCollection<ConstellationLine> Lines { get; set; } = new();
    public BinaryConvertibleCollection<ConstellationLabel> Labels { get; set; } = new();

    public override string Name => "ConstellationSet";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ConstellationSet()
    {
        d.RegisterField("Lines", (ConstellationSet x) => x.Lines);
        d.RegisterField("Labels", (ConstellationSet x) => x.Labels);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A search hit: a star or a deep-sky object with where it is.</summary>
public class AtlasHit : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "";
    public BinaryConvertibleString Detail { get; set; } = "";
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleFloat Magnitude { get; set; } = float.NaN;
    public BinaryConvertibleFloat MajorArcmin { get; set; } = 0f;

    public override string Name => "AtlasHit";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasHit()
    {
        d.RegisterField("Label", (AtlasHit x) => x.Label).Description("e.g. M 42, Vega");
        d.RegisterField("Kind", (AtlasHit x) => x.Kind).Description("Star or one of the deep-sky kinds");
        d.RegisterField("Detail", (AtlasHit x) => x.Detail).Description("other names");
        d.RegisterField("RaHours", (AtlasHit x) => x.RaHours).Description("J2000");
        d.RegisterField("DecDegrees", (AtlasHit x) => x.DecDegrees).Description("J2000");
        d.RegisterField("Magnitude", (AtlasHit x) => x.Magnitude);
        d.RegisterField("MajorArcmin", (AtlasHit x) => x.MajorArcmin);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class AtlasHits : IBinaryConvertible
{
    public BinaryConvertibleCollection<AtlasHit> Hits { get; set; } = new();

    public override string Name => "AtlasHits";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static AtlasHits() { d.RegisterField("Hits", (AtlasHits x) => x.Hits, maxCount: 200); }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>What to image tonight: asks the atlas which deep-sky objects are best placed in tonight's dark hours at your site, for your frame.</summary>
public class TonightRequest : IBinaryConvertible
{
    public BinaryConvertibleDouble FovWidthDegrees { get; set; } = 0.0;
    public BinaryConvertibleDouble FovHeightDegrees { get; set; } = 0.0;
    public BinaryConvertibleInt32 MaxResults { get; set; } = 12;
    public BinaryConvertibleDouble MagnitudeLimit { get; set; } = 11.0;
    public BinaryConvertibleString Kinds { get; set; } = "";
    public BinaryConvertibleDouble MinAltitudeDegrees { get; set; } = 25.0;

    public override string Name => "TonightRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TonightRequest()
    {
        d.RegisterField("FovWidthDegrees", (TonightRequest x) => x.FovWidthDegrees).Description("the frame to fit: your smallest scope's; 0 = 1.5 by 1 degrees");
        d.RegisterField("FovHeightDegrees", (TonightRequest x) => x.FovHeightDegrees);
        d.RegisterField("MaxResults", (TonightRequest x) => x.MaxResults).Range(1, 50);
        d.RegisterField("MagnitudeLimit", (TonightRequest x) => x.MagnitudeLimit).Description("the faintest object considered (total magnitude)");
        d.RegisterField("Kinds", (TonightRequest x) => x.Kinds).Description("comma separated: Galaxy, Nebula, PlanetaryNebula, SupernovaRemnant, OpenCluster, GlobularCluster; empty = all of them");
        d.RegisterField("MinAltitudeDegrees", (TonightRequest x) => x.MinAltitudeDegrees).Description("only time above this (and above the site's horizon) counts as good");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class TonightTarget : IBinaryConvertible
{
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Kind { get; set; } = "";
    public BinaryConvertibleString CommonName { get; set; } = "";
    public BinaryConvertibleDouble RaHours { get; set; } = 0.0;
    public BinaryConvertibleDouble DecDegrees { get; set; } = 0.0;
    public BinaryConvertibleFloat Magnitude { get; set; } = float.NaN;
    public BinaryConvertibleFloat MajorArcmin { get; set; } = 0f;
    public BinaryConvertibleDouble GoodHours { get; set; } = 0.0;
    public BinaryConvertibleDouble PeakAltitude { get; set; } = 0.0;
    public BinaryConvertibleString PeakUtc { get; set; } = "";
    public BinaryConvertibleDouble MoonSeparationDegrees { get; set; } = double.NaN;
    public BinaryConvertibleDouble FrameFraction { get; set; } = 0.0;
    public BinaryConvertibleInt32 Panels { get; set; } = 1;
    public BinaryConvertibleDouble Score { get; set; } = 0.0;
    public BinaryConvertibleString Why { get; set; } = "";

    public override string Name => "TonightTarget";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TonightTarget()
    {
        d.RegisterField("Label", (TonightTarget x) => x.Label);
        d.RegisterField("Kind", (TonightTarget x) => x.Kind);
        d.RegisterField("CommonName", (TonightTarget x) => x.CommonName);
        d.RegisterField("RaHours", (TonightTarget x) => x.RaHours).Description("J2000");
        d.RegisterField("DecDegrees", (TonightTarget x) => x.DecDegrees);
        d.RegisterField("Magnitude", (TonightTarget x) => x.Magnitude);
        d.RegisterField("MajorArcmin", (TonightTarget x) => x.MajorArcmin);
        d.RegisterField("GoodHours", (TonightTarget x) => x.GoodHours).Description("hours of the dark period it is above the horizon and high enough, higher counting more");
        d.RegisterField("PeakAltitude", (TonightTarget x) => x.PeakAltitude);
        d.RegisterField("PeakUtc", (TonightTarget x) => x.PeakUtc);
        d.RegisterField("MoonSeparationDegrees", (TonightTarget x) => x.MoonSeparationDegrees);
        d.RegisterField("FrameFraction", (TonightTarget x) => x.FrameFraction).Description("its size as a fraction of the frame's smaller side");
        d.RegisterField("Panels", (TonightTarget x) => x.Panels).Description("roughly how many panels it takes to cover it with the frame");
        d.RegisterField("Score", (TonightTarget x) => x.Score).Description("0..100");
        d.RegisterField("Why", (TonightTarget x) => x.Why).Description("what makes it a good one, in words");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class TonightList : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleString DarkFromUtc { get; set; } = "";
    public BinaryConvertibleString DarkToUtc { get; set; } = "";
    public BinaryConvertibleDouble MoonIllumination { get; set; } = double.NaN;
    public BinaryConvertibleCollection<TonightTarget> Targets { get; set; } = new();

    public override string Name => "TonightList";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static TonightList()
    {
        d.RegisterField("Ok", (TonightList x) => x.Ok);
        d.RegisterField("Message", (TonightList x) => x.Message);
        d.RegisterField("DarkFromUtc", (TonightList x) => x.DarkFromUtc).Description("the dark period the list is for");
        d.RegisterField("DarkToUtc", (TonightList x) => x.DarkToUtc);
        d.RegisterField("MoonIllumination", (TonightList x) => x.MoonIllumination);
        d.RegisterField("Targets", (TonightList x) => x.Targets, maxCount: 50).Description("best first");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class AtlasIds
{
    public const string Root = "ELink.Atlas";
    /// <summary>AtlasQuery in, AtlasChunk out.</summary>
    public const string Query = Root + ".Query";
    /// <summary>Void in, ConstellationSet out.</summary>
    public const string Constellations = Root + ".Constellations";
    /// <summary>BinaryConvertibleString (a name or designation, e.g. "M42", "Vega", "NGC 7000", "andromeda") in, AtlasHits out.</summary>
    public const string Search = Root + ".Search";
    /// <summary>TonightRequest in, TonightList out: the best objects to image in tonight's dark hours at the site.</summary>
    public const string Tonight = Root + ".Tonight";
}
