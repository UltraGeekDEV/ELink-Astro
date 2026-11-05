using ELink.Automation;
using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>A target with no size is one frame as the camera is turned on the sky. With the camera at 90° the area used to keep the
/// frame's unturned width and height, its corners were never covered, and the image ended Done with its thinnest spot at 0 s.</summary>
public class FrameTurnTests
{
    private static readonly FrameSpec Apsc = new(1.49 / 2, 0.99 / 2, 0, 0, 0);

    [Fact]
    public void UprightFrameCoversItsOwnSize()
    {
        var (w, h) = ImagingService.InscribedUpright(Apsc, 0);
        Assert.Equal(1.49, w, 6); Assert.Equal(0.99, h, 6);
        Assert.True(ImagingService.Covers(Apsc, 0, 1.49 * 0.9, 0.99 * 0.9));
    }

    [Fact]
    public void AFrameOnEndCoversTheTurnedSizeNotItsOwn()
    {
        var (w, h) = ImagingService.InscribedUpright(Apsc, 90);
        Assert.Equal(0.99, w, 6); Assert.Equal(1.49, h, 6);
        Assert.False(ImagingService.Covers(Apsc, 90, 1.49 * 0.9, 0.99 * 0.9));   // the old default area
        Assert.True(ImagingService.Covers(Apsc, 90, w * 0.9, h * 0.9));
    }

    [Theory]
    [InlineData(30)] [InlineData(45)] [InlineData(-60)] [InlineData(135)]
    public void AtAnyAngleTheDefaultAreaIsCoveredWhole(double angle)
    {
        var (w, h) = ImagingService.InscribedUpright(Apsc, angle);
        Assert.True(w > 0 && h > 0);
        Assert.True(ImagingService.Covers(Apsc, angle, w, h));
        Assert.False(ImagingService.Covers(Apsc, angle, w * 1.01, h * 1.01));   // and it is the largest such area
    }
}
