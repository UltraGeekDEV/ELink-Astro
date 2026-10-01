using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests;

public class SexagesimalTests
{
    [Theory]
    [InlineData("12:30:15", 12.504166667)] [InlineData("12 30 15", 12.504166667)] [InlineData("12h30m15s", 12.504166667)]
    [InlineData("-5°30'", -5.5)] [InlineData("-0:30", -0.5)] [InlineData("3.5", 3.5)] [InlineData("  7:00  ", 7)]
    public void Parses(string text, double expected)
    {
        Assert.True(Sexagesimal.TryParse(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory] [InlineData("")] [InlineData("abc")] [InlineData("1:2:3:4")] [InlineData("1:-2")]
    public void RejectsGarbage(string text) => Assert.False(Sexagesimal.TryParse(text, out _));

    [Fact]
    public void Formats()
    {
        Assert.Equal("12:30:15.0", Sexagesimal.Format(12.5041666667));
        Assert.Equal("-00:30:00.0", Sexagesimal.Format(-0.5));
        Assert.Equal("01:00:00", Sexagesimal.Format(0.99999999, 0));
        Assert.Equal("--", Sexagesimal.Format(double.NaN));
    }
}
