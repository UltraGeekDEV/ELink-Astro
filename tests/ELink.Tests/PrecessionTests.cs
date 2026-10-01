using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests;

public class PrecessionTests
{
    [Fact]
    public void J2000IsIdentityAtJ2000()
    {
        var (ra, dec) = Precession.J2000ToDate(5.5, 20, new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(5.5, ra, 6); Assert.Equal(20, dec, 5);
    }

    [Fact]
    public void KnownShiftAfterTwentySixYears()
    {
        // RA 0h Dec 0 moves ~3.07 s/yr in RA and ~20"/yr in Dec: after 26.75 years ~82 s and ~+535".
        var (ra, dec) = Precession.J2000ToDate(0, 0, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.InRange(ra * 3600, 70, 95);
        Assert.InRange(dec * 3600, 450, 620);
    }

    [Theory]
    [InlineData(0.1, 80)] [InlineData(12, -45)] [InlineData(23.9, 10)] [InlineData(5.5, 89)]
    public void RoundTrips(double ra, double dec)
    {
        var t = new DateTime(2031, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var (rd, dd) = Precession.J2000ToDate(ra, dec, t);
        var (rb, db) = Precession.DateToJ2000(rd, dd, t);
        Assert.Equal(ra, rb, 7); Assert.Equal(dec, db, 6);
    }
}
