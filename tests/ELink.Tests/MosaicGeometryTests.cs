using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core.Astro;
using Xunit;

namespace ELink.Tests;

public class MosaicGeometryTests
{
    public static MosaicRequest Req(double fovW, double fovH, double frameW = 0.5, double frameH = 0.5, double overlap = 0.2, double pa = 0, double ra = 5.5, double dec = 20) => new()
    {
        Center = new SkyTarget { RaHours = ra, DecDegrees = dec, Epoch = "J2000" },
        FovWidthDegrees = fovW, FovHeightDegrees = fovH, FrameWidthDegrees = frameW, FrameHeightDegrees = frameH, Overlap = overlap, PositionAngleDegrees = pa,
    };

    [Theory]
    [InlineData(0.4, 0.4, 1, 1)]       // fits in one frame
    [InlineData(0.5, 0.5, 1, 1)]
    [InlineData(1.0, 0.5, 3, 1)]       // step 0.4: (1.0-0.5)/0.4 = 1.25 -> 2 steps -> 3 columns
    [InlineData(2.0, 1.0, 5, 2)]       // (2.0-0.5)/0.4 = 3.75 -> 4 -> 5 cols ; (1.0-0.5)/0.4=1.25 -> 2 -> 3 rows? see below
    public void CountsPanelsToCoverTheField(double w, double h, int cols, int rows)
    {
        var l = MosaicGeometry.Plan(Req(w, h));
        Assert.Equal("", l.Error.Text);
        Assert.Equal(cols, l.Cols.Value);
        Assert.Equal(w == 2.0 ? 3 : rows, l.Rows.Value);
        Assert.Equal(l.Rows.Value * l.Cols.Value, l.Panels.Count);
    }

    [Fact]
    public void NeighbouringPanelsAreOneStepApartAndTheGridIsCentred()
    {
        var l = MosaicGeometry.Plan(Req(2.0, 1.0));
        Assert.Equal(0.4, l.StepXDegrees.Value, 9);
        var byRc = l.Panels.ToDictionary(p => (p.Row.Value, p.Col.Value));
        double Sep((int, int) a, (int, int) b) => Sky.SeparationDegrees(byRc[a].RaHours.Value, byRc[a].DecDegrees.Value, byRc[b].RaHours.Value, byRc[b].DecDegrees.Value);
        Assert.Equal(0.4, Sep((0, 0), (0, 1)), 3);
        Assert.Equal(0.4, Sep((0, 0), (1, 0)), 3);
        // the middle column / row sits on the centre
        Assert.Equal(5.5, byRc[(1, 2)].RaHours.Value, 4);
        Assert.Equal(20, byRc[(1, 2)].DecDegrees.Value, 3);
        // east grows with the column: RA increases
        Assert.True(byRc[(1, 4)].RaHours.Value > byRc[(1, 0)].RaHours.Value);
        // north is row 0: declination decreases with the row
        Assert.True(byRc[(0, 2)].DecDegrees.Value > byRc[(2, 2)].DecDegrees.Value);
    }

    [Fact]
    public void EveryPointOfTheFieldIsInsideSomeFrame()
    {
        var req = Req(1.7, 1.1, pa: 30);
        var l = MosaicGeometry.Plan(req);
        var rnd = new Random(4);
        for (int i = 0; i < 400; i++)
        {
            double x = (rnd.NextDouble() - 0.5) * 1.7, y = (rnd.NextDouble() - 0.5) * 1.1;
            double pa = 30 * Math.PI / 180;
            double east = x * Math.Cos(pa) + y * Math.Sin(pa), north = -x * Math.Sin(pa) + y * Math.Cos(pa);
            var (ra, dec) = Gnomonic.ToSky(5.5, 20, east, north);
            bool covered = l.Panels.Any(p =>
            {
                var (e, n) = Gnomonic.FromSky(p.RaHours.Value, p.DecDegrees.Value, ra, dec);
                // rotate into the panel's (mosaic) frame, the frames are aligned with the mosaic
                double ex = e * Math.Cos(pa) - n * Math.Sin(pa), ny = e * Math.Sin(pa) + n * Math.Cos(pa);
                return Math.Abs(ex) <= 0.25 + 1e-6 && Math.Abs(ny) <= 0.25 + 1e-6;
            });
            Assert.True(covered, $"point ({x:0.00},{y:0.00}) is not covered");
        }
    }

    [Fact]
    public void RotationTurnsTheWholeMosaic()
    {
        var l0 = MosaicGeometry.Plan(Req(1.5, 0.5, frameW: 0.5, frameH: 0.5, pa: 0));
        var l90 = MosaicGeometry.Plan(Req(1.5, 0.5, frameW: 0.5, frameH: 0.5, pa: 90));
        // a row of 4 along the east-west direction becomes a column along north-south
        double Span(MosaicLayout l, bool ra) => ra ? (l.Panels.Max(p => p.RaHours.Value) - l.Panels.Min(p => p.RaHours.Value)) * 15 : l.Panels.Max(p => p.DecDegrees.Value) - l.Panels.Min(p => p.DecDegrees.Value);
        Assert.True(Span(l0, true) > 0.5 && Span(l0, false) < 0.01);
        Assert.True(Span(l90, false) > 0.5 && Span(l90, true) * Math.Cos(20 * Math.PI / 180) < 0.01);
    }

    [Fact]
    public void WorksAcrossRaWrapAndInTheSouth()
    {
        var l = MosaicGeometry.Plan(Req(2, 1, ra: 0.01, dec: -60));
        Assert.Equal("", l.Error.Text);
        Assert.All(l.Panels, p => Assert.InRange(p.RaHours.Value, 0, 24));
        Assert.Contains(l.Panels, p => p.RaHours.Value > 23.5);       // wrapped below 0h
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)]
    public void RejectsNonsense(double w, double h) => Assert.NotEqual("", MosaicGeometry.Plan(Req(w, h)).Error.Text);

    [Fact]
    public void RejectsPolesTooManyPanelsAndBadOverlap()
    {
        Assert.Contains("pole", MosaicGeometry.Plan(Req(2, 2, dec: 89.5)).Error.Text);
        Assert.Contains("panels", MosaicGeometry.Plan(Req(60, 60, frameW: 0.2, frameH: 0.2)).Error.Text);
        Assert.NotEqual("", MosaicGeometry.Plan(Req(1, 1, overlap: 0.95)).Error.Text);
        var jnow = Req(1, 1); jnow.Center.Epoch = "JNow";
        Assert.Contains("J2000", MosaicGeometry.Plan(jnow).Error.Text);
    }

    [Theory]
    [InlineData(3, 4, 1)] [InlineData(3, 4, 2)] [InlineData(1, 5, 1)] [InlineData(4, 1, 2)] [InlineData(5, 5, 3)]
    public void ScanOrderCoversEveryPanelOnceAndEveryStepIsToANeighbour(int rows, int cols, int pass)
    {
        var order = MosaicGeometry.ScanOrder(rows, cols, pass).ToList();
        Assert.Equal(rows * cols, order.Distinct().Count());
        for (int i = 1; i < order.Count; i++)
            Assert.Equal(1, Math.Abs(order[i].Row - order[i - 1].Row) + Math.Abs(order[i].Col - order[i - 1].Col));
    }

    [Fact]
    public void ConsecutivePassesJoinWithoutAJump()
    {
        var p1 = MosaicGeometry.ScanOrder(3, 4, 1).Last();
        var p2 = MosaicGeometry.ScanOrder(3, 4, 2).First();
        Assert.Equal(p1, p2);                    // the last panel of a pass is the first of the next: no slew at all
    }
}
