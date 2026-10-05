using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Contracts.Automation;

/// <summary>What to do to the linear stack to make a picture of it (the stack itself is never changed).</summary>
public class ProcessingSettings : IBinaryConvertible
{
    public BinaryConvertibleBool Stretch { get; set; } = true;
    public BinaryConvertibleDouble BackgroundLevel { get; set; } = 0.25;
    public BinaryConvertibleDouble BlackClipSigmas { get; set; } = 2.8;
    public BinaryConvertibleBool Linked { get; set; } = true;
    public BinaryConvertibleBool RemoveGradient { get; set; } = true;
    public BinaryConvertibleInt32 GradientDegree { get; set; } = 3;
    public BinaryConvertibleBool GradientDivide { get; set; } = false;
    public BinaryConvertibleBool NeutralizeBackground { get; set; } = true;
    public BinaryConvertibleDouble Saturation { get; set; } = 1.0;
    public BinaryConvertibleDouble GreenReduction { get; set; } = 0.0;

    public override string Name => "ProcessingSettings";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ProcessingSettings()
    {
        d.RegisterField("Stretch", (ProcessingSettings x) => x.Stretch).Description("an automatic stretch (midtones transfer); false = a linear scaling");
        d.RegisterField("BackgroundLevel", (ProcessingSettings x) => x.BackgroundLevel).Description("where the sky background ends up, 0.05..0.6").Range(0.02m, 0.9m);
        d.RegisterField("BlackClipSigmas", (ProcessingSettings x) => x.BlackClipSigmas).Description("the black point, in noise sigmas below the sky").Range(0m, 8m);
        d.RegisterField("Linked", (ProcessingSettings x) => x.Linked).Description("one stretch for all colours (keeps the colour balance)");
        d.RegisterField("RemoveGradient", (ProcessingSettings x) => x.RemoveGradient).Description("model the sky background and take it away");
        d.RegisterField("GradientDegree", (ProcessingSettings x) => x.GradientDegree).Description("how flexible the model is, 1 (a tilt) .. 6").Range(1, 6);
        d.RegisterField("GradientDivide", (ProcessingSettings x) => x.GradientDivide).Description("divide by the model (vignetting) instead of subtracting it (sky glow)");
        d.RegisterField("NeutralizeBackground", (ProcessingSettings x) => x.NeutralizeBackground).Description("make the sky the same level in every colour");
        d.RegisterField("Saturation", (ProcessingSettings x) => x.Saturation).Description("1 = as it is").Range(0m, 3m);
        d.RegisterField("GreenReduction", (ProcessingSettings x) => x.GreenReduction).Description("take a green cast out, 0..1").Range(0m, 1m);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>A picture of the live stack, processed, as a PNG that fits the given size.</summary>
public class ProcessRequest : IBinaryConvertible
{
    public ProcessingSettings Settings { get; set; } = new();
    public BinaryConvertibleInt32 MaxWidth { get; set; } = 1600;
    public BinaryConvertibleInt32 MaxHeight { get; set; } = 1200;
    public BinaryConvertibleString Source { get; set; } = "";
    public BinaryConvertibleDouble OutOfFocusWeight { get; set; } = 0.0;
    public BinaryConvertibleString PseudoOutput { get; set; } = "";

    public override string Name => "ProcessRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ProcessRequest()
    {
        d.RegisterField("Settings", (ProcessRequest x) => x.Settings);
        d.RegisterField("MaxWidth", (ProcessRequest x) => x.MaxWidth).Description("the picture is reduced to fit; 0 = full size");
        d.RegisterField("MaxHeight", (ProcessRequest x) => x.MaxHeight);
        d.RegisterField("Source", (ProcessRequest x) => x.Source).Description("which stack: a filter or layer name, Combined, or empty for what the live stack shows by default");
        d.RegisterField("OutOfFocusWeight", (ProcessRequest x) => x.OutOfFocusWeight).Description("pseudo mono: how much out-of-focus light goes in");
        d.RegisterField("PseudoOutput", (ProcessRequest x) => x.PseudoOutput).Description("pseudo mono: Colour | Luminance | Sharp");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ProcessedImage : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleInt32 Width { get; set; } = 0;
    public BinaryConvertibleInt32 Height { get; set; } = 0;
    public BinaryConvertibleInt32 Channels { get; set; } = 0;
    public BinaryConvertibleInt32 Frames { get; set; } = 0;
    public BinaryConvertibleDouble PixelScaleArcsec { get; set; } = 0.0;
    public BinaryConvertibleDouble ExposureSeconds { get; set; } = 0.0;
    public BinaryConvertibleBool FlipY { get; set; } = false;
    public BinaryConvertibleDouble CropLeft { get; set; } = 0.0;
    public BinaryConvertibleDouble CropTop { get; set; } = 0.0;
    public BinaryConvertibleDouble CropWidth { get; set; } = 1.0;
    public BinaryConvertibleDouble CropHeight { get; set; } = 1.0;
    public BinaryConvertibleDouble GradientPercent { get; set; } = 0.0;
    public BinaryConvertibleString Note { get; set; } = "";
    public BinaryConvertibleString Source { get; set; } = "";
    public RawBytes Png { get; set; } = new();
    public RawBytes Histogram { get; set; } = new();

    public override string Name => "ProcessedImage";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ProcessedImage()
    {
        d.RegisterField("Ok", (ProcessedImage x) => x.Ok);
        d.RegisterField("Message", (ProcessedImage x) => x.Message);
        d.RegisterField("Width", (ProcessedImage x) => x.Width);
        d.RegisterField("Height", (ProcessedImage x) => x.Height);
        d.RegisterField("Channels", (ProcessedImage x) => x.Channels);
        d.RegisterField("Frames", (ProcessedImage x) => x.Frames);
        d.RegisterField("PixelScaleArcsec", (ProcessedImage x) => x.PixelScaleArcsec);
        d.RegisterField("ExposureSeconds", (ProcessedImage x) => x.ExposureSeconds).Description("total exposure of the stack shown");
        d.RegisterField("FlipY", (ProcessedImage x) => x.FlipY).Description("the picture's rows are the stack's rows last to first: a position on the stack grid (StackFrameAdded) is 1 - y on the picture");
        d.RegisterField("CropLeft", (ProcessedImage x) => x.CropLeft).Description("the picture shows only the part of the stack that has data: this part, as fractions of the stack grid's width and height (rows as the grid is stored)");
        d.RegisterField("CropTop", (ProcessedImage x) => x.CropTop); d.RegisterField("CropWidth", (ProcessedImage x) => x.CropWidth); d.RegisterField("CropHeight", (ProcessedImage x) => x.CropHeight);
        d.RegisterField("GradientPercent", (ProcessedImage x) => x.GradientPercent).Description("how much of the sky level the background model removed");
        d.RegisterField("Note", (ProcessedImage x) => x.Note).Description("what was done, in words");
        d.RegisterField("Source", (ProcessedImage x) => x.Source).Description("which stack it is of");
        d.RegisterField("Png", (ProcessedImage x) => x.Png).Description("8-bit PNG, first row on top");
        d.RegisterField("Histogram", (ProcessedImage x) => x.Histogram).Description("128 bins per channel, 0..255 of the highest, of the picture's values");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

/// <summary>Save the stack: the linear, unstretched data (always) and the processed picture.</summary>
public class ExportRequest : IBinaryConvertible
{
    public ProcessingSettings Settings { get; set; } = new();
    public BinaryConvertibleString Source { get; set; } = "";
    public BinaryConvertibleString Label { get; set; } = "";
    public BinaryConvertibleString Directory { get; set; } = "";
    public BinaryConvertibleDouble OutOfFocusWeight { get; set; } = 0.0;
    public BinaryConvertibleString PseudoOutput { get; set; } = "";
    public BinaryConvertibleBool PictureToo { get; set; } = true;

    public override string Name => "ExportRequest";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ExportRequest()
    {
        d.RegisterField("Settings", (ExportRequest x) => x.Settings);
        d.RegisterField("Source", (ExportRequest x) => x.Source);
        d.RegisterField("Label", (ExportRequest x) => x.Label).Description("names the files; empty = the stack's label");
        d.RegisterField("Directory", (ExportRequest x) => x.Directory).Description("where to put them; empty = the pictures folder of the station");
        d.RegisterField("OutOfFocusWeight", (ExportRequest x) => x.OutOfFocusWeight);
        d.RegisterField("PseudoOutput", (ExportRequest x) => x.PseudoOutput);
        d.RegisterField("PictureToo", (ExportRequest x) => x.PictureToo).Description("also write the processed picture (a 16-bit PNG); the linear FITS is always written");
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public class ExportResult : IBinaryConvertible
{
    public BinaryConvertibleBool Ok { get; set; } = false;
    public BinaryConvertibleString Message { get; set; } = "";
    public BinaryConvertibleCollection<BinaryConvertibleString> Files { get; set; } = new();

    public override string Name => "ExportResult";
    private static readonly NOTESDescriptor d = new();
    public override NOTESDescriptor Descriptor => d;
    static ExportResult()
    {
        d.RegisterField("Ok", (ExportResult x) => x.Ok);
        d.RegisterField("Message", (ExportResult x) => x.Message);
        d.RegisterField("Files", (ExportResult x) => x.Files, maxCount: 8);
    }
    public override bool FromBytes(ref Span<byte> data) => d.FromBytes(this, ref data);
    public override byte[] ToBytes() => d.ToBytes(this).ToArray();
}

public static class ProcessingIds
{
    public const string Root = "ELink.Automation.Processing";
    /// <summary>ProcessRequest in, ProcessedImage out: a picture of the live stack.</summary>
    public const string Process = Root + ".Process";
    /// <summary>ExportRequest in, ExportResult out: the linear stack and the picture, saved.</summary>
    public const string Export = Root + ".Export";
}
