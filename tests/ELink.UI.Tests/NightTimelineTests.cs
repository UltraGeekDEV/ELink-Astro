using ELink.Contracts.Site;
using ELink.UI.ViewModels;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The bar on the Site page: when it is dark and when the Moon is up over the next 24 hours.</summary>
public class NightTimelineTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 22, 0, 0, DateTimeKind.Local);
    private static string Iso(double hoursFromNow) => Now.AddHours(hoursFromNow).ToUniversalTime().ToString("o");

    [Fact]
    public void ItIsAlreadyDarkTheNightEndsAtDawnAndTheNextOneBegins()
    {
        var s = new SiteState { Sky = "Night", DuskUtc = Iso(20), DawnUtc = Iso(7), MoonAltitude = -10, MoonRiseUtc = Iso(3), MoonSetUtc = Iso(15) };
        var t = SiteViewModel.Timeline(s, Now);
        Assert.Equal(Now.AddHours(-2), t.From); Assert.Equal(Now.AddHours(22), t.To);
        Assert.Equal(2, t.Dark.Count);
        Assert.Equal((t.From, Now.AddHours(7)), (t.Dark[0].Start, t.Dark[0].End));           // dark since before the bar began, until dawn
        Assert.Equal((Now.AddHours(20), t.To), (t.Dark[1].Start, t.Dark[1].End));            // and the next night, cut by the end of the bar
        Assert.Equal((Now.AddHours(3), Now.AddHours(15)), (t.MoonUp.Single().Start, t.MoonUp.Single().End));
    }

    [Fact]
    public void ItIsDayTheDarkHoursAreDuskToDawnAndTheMoonIsUpNow()
    {
        var s = new SiteState { Sky = "Day", DuskUtc = Iso(6), DawnUtc = Iso(14), MoonAltitude = 30, MoonSetUtc = Iso(4), MoonRiseUtc = Iso(25) };
        var t = SiteViewModel.Timeline(s, Now);
        var dark = Assert.Single(t.Dark);
        Assert.Equal((Now.AddHours(6), Now.AddHours(14)), (dark.Start, dark.End));
        var moon = Assert.Single(t.MoonUp);                                                   // up now, sets in 4 h; the next rise is beyond the bar
        Assert.Equal((t.From, Now.AddHours(4)), (moon.Start, moon.End));
    }

    [Fact]
    public void WithoutEventsThereIsNoDarkAndAlwaysNightIsAllDark()
    {
        Assert.Empty(SiteViewModel.Timeline(new SiteState { Sky = "Day" }, Now).Dark);
        var all = Assert.Single(SiteViewModel.Timeline(new SiteState { Sky = "Night" }, Now).Dark);   // (a polar winter: no dawn within the window)
        Assert.Equal((Now.AddHours(-2), Now.AddHours(22)), (all.Start, all.End));
    }
}
