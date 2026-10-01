using ELink.Automation;
using ELink.Core.Astro;
using Xunit;
using Xunit.Abstractions;

namespace ELink.Tests;

public class CoveragePlannerTests(ITestOutputHelper output)
{
    private static FrameSpec Frame(double w, double h, double rot = 0, double oe = 0, double on = 0) => new(w / 2, h / 2, rot, oe, on);

    private sealed record Run(List<Pose> Visits, CoveragePlanner Planner)
    {
        public double Hop(int i) => Math.Sqrt(Math.Pow(Visits[i].X - Visits[i - 1].X, 2) + Math.Pow(Visits[i].Y - Visits[i - 1].Y, 2));
    }

    private Run Simulate(double fovW, double fovH, FrameSpec[] frames, double exposure, double target, double stepover,
        double areaAngle = 0, double[]? rotations = null, double cell = 0.02, int maxVisits = 5000)
    {
        var map = new CoverageMap(fovW, fovH, cell);
        var p = new CoveragePlanner(map, frames, rotations ?? Array.Empty<double>(), areaAngle, exposure, target, stepover);
        var visits = new List<Pose>();
        while (visits.Count < maxVisits && p.Next() is { } pose) visits.Add(pose);
        output.WriteLine($"visits={visits.Count} min={map.Min():0.##} mean={map.Mean():0.##} max={map.Max():0.##} done={p.Done}");
        return new Run(visits, p);
    }

    private int LongHops(Run r) => Enumerable.Range(1, r.Visits.Count - 1).Count(i => r.Hop(i) > 1.6 * (double.IsNaN(r.Planner.PassHop) ? r.Planner.Stepover : r.Planner.PassHop));

    [Fact]
    public void PaintsASingleFrameFieldToTheTargetEvenly()
    {
        var r = Simulate(1.5, 1.0, new[] { Frame(0.5, 0.5) }, exposure: 1, target: 12, stepover: 0.05);
        var map = r.Planner.Map;
        Assert.True(r.Planner.Done);
        Assert.True(map.Min() >= 12);
        Assert.True(map.Mean() <= 12 * 1.1, $"mean {map.Mean()}");           // little waste
        Assert.True(map.Max() <= 12 * 1.45, $"max {map.Max()}");             // and no hot spots
        Assert.InRange(r.Visits.Count, 100, 200);
        // the scope sweeps in steps of the pass hop; long jumps are the exception
        Assert.True(LongHops(r) <= r.Visits.Count * 0.15, $"{LongHops(r)} long hops in {r.Visits.Count} visits");
    }

    [Fact]
    public void ADeepTargetMeansSeveralLightPassesWithSmallHops()
    {
        var r = Simulate(1.5, 1.0, new[] { Frame(0.5, 0.5) }, exposure: 1, target: 250, stepover: 0.05, cell: 0.025, maxVisits: 20000);
        Assert.True(r.Planner.Done);
        Assert.True(r.Planner.PassCount >= 2, $"passes {r.Planner.PassCount}");
        Assert.InRange(Math.Max(r.Planner.PassHopX, r.Planner.PassHopY), 0.05, 0.11);   // small stepovers: a tenth to a fifth of the frame
        Assert.True(r.Planner.Map.Max() <= 250 * 1.15, $"max {r.Planner.Map.Max()}");
        Assert.True(r.Planner.Map.Mean() <= 250 * 1.04);
        // consecutive visits are neighbours within the hop (the serpentine route), apart from occasional skips of useless poses
        double maxHop = Math.Max(r.Planner.PassHopX, r.Planner.PassHopY) * 1.001;
        int near = Enumerable.Range(1, r.Visits.Count - 1).Count(i => r.Hop(i) <= maxHop);
        Assert.True(near >= r.Visits.Count * 0.8, $"{near} of {r.Visits.Count} hops are single steps");
    }

    [Fact]
    public void HandlesHeterogeneousRotatedFramesAndFieldRotations()
    {
        var frames = new[]
        {
            Frame(0.5, 0.5),
            Frame(0.3, 0.2, rot: 30, oe: 0.1, on: -0.05),
            Frame(0.6, 0.4, rot: 90),
        };
        var r = Simulate(1.6, 1.2, frames, exposure: 1, target: 60, stepover: 0.06, areaAngle: 20, rotations: new[] { 0.0, 45.0, 90.0 }, maxVisits: 20000);
        Assert.True(r.Planner.Done);
        Assert.True(r.Planner.Map.Min() >= 60);
        Assert.True(r.Planner.Map.Max() <= 60 * 1.35, $"max {r.Planner.Map.Max()}");
        Assert.Equal(new[] { 0.0, 45.0, 90.0 }, r.Visits.Select(v => v.FieldAngle).Distinct().Order().ToArray());   // all three rotations were used
        // the rotator turns between passes, never within one
        int turns = Enumerable.Range(1, r.Visits.Count - 1).Count(i => r.Visits[i].FieldAngle != r.Visits[i - 1].FieldAngle);
        Assert.True(turns <= r.Planner.PassCount + 8, $"{turns} rotator moves for {r.Planner.PassCount} passes");
    }

    [Fact]
    public void EndlessModeSweepsPassAfterPassWithoutLeavingAnythingBehind()
    {
        var r = Simulate(1.2, 0.8, new[] { Frame(0.4, 0.4) }, exposure: 1, target: 0, stepover: 0.2, maxVisits: 400);
        var map = r.Planner.Map;
        Assert.Equal(400, r.Visits.Count);
        Assert.True(map.Min() > 0.5 * map.Mean(), $"min {map.Min()} vs mean {map.Mean()}");
        Assert.False(r.Planner.Done);
    }

    [Fact]
    public void RetargetingWhileRunningContinuesThePainting()
    {
        var map = new CoverageMap(1.0, 0.8, 0.02);
        var p = new CoveragePlanner(map, new[] { Frame(0.4, 0.4) }, Array.Empty<double>(), 0, 1, 5, 0.04);
        while (p.Next() is not null) { }
        Assert.True(map.Min() >= 5);
        p.TargetSeconds = 10;                                  // asked for more
        p.Plan();
        Assert.False(p.Done);
        while (p.Next() is not null) { }
        Assert.True(map.Min() >= 10);
        Assert.True(map.Mean() <= 10 * 1.2);                   // only the missing depth was added
    }

    [Fact]
    public void RotationsAlternateFromPassToPassSoTheFieldIsPaintedFromSeveralAngles()
    {
        var frames = new[] { Frame(0.2, 0.8) };
        var r = Simulate(2.0, 0.6, frames, 1, 60, 0.05, rotations: new[] { 0.0, 90.0 }, cell: 0.025);
        Assert.True(r.Planner.Done);
        Assert.Contains(r.Visits, v => v.FieldAngle == 90.0);
        Assert.Contains(r.Visits, v => v.FieldAngle == 0.0);
        Assert.True(r.Planner.Map.Max() <= 60 * 1.3, $"max {r.Planner.Map.Max()}");
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Simulate(1.0, 0.8, new[] { Frame(0.4, 0.3, 15) }, 1, 6, 0.04, rotations: new[] { 0.0, 60.0 });
        var b = Simulate(1.0, 0.8, new[] { Frame(0.4, 0.3, 15) }, 1, 6, 0.04, rotations: new[] { 0.0, 60.0 });
        Assert.Equal(a.Visits, b.Visits);
    }

    [Fact]
    public void BeginsInTheNorthWestAndStaysWithinReachOfTheArea()
    {
        var r = Simulate(1.5, 1.0, new[] { Frame(0.5, 0.5) }, 1, 6, 0.05);
        Assert.True(r.Visits[0].X < 0 && r.Visits[0].Y > 0, $"first pose {r.Visits[0]}");
        double reach = Math.Sqrt(0.25 * 0.25 * 2) + 0.01;
        Assert.All(r.Visits, v => { Assert.InRange(v.X, -0.75 - reach, 0.75 + reach); Assert.InRange(v.Y, -0.5 - reach, 0.5 + reach); });
    }

    [Fact]
    public void FootprintsFollowRotationOffsetsAndAreaAngle()
    {
        var fp = CoverageMap.Footprints(new Pose(1, 2, 90), new[] { Frame(0.4, 0.2, rot: 0, oe: 0.1, on: 0) }, areaAngle: 0);
        // field turned 90 degrees: the scope's east axis points along the area's -y, so the offset (0.1 east) lands below the pose
        Assert.Equal(1.0, fp[0].Cx, 6); Assert.Equal(1.9, fp[0].Cy, 6);
        Assert.Equal(Math.PI / 2, fp[0].Theta, 6);
        var aligned = CoverageMap.Footprints(new Pose(0, 0, 30), new[] { Frame(0.4, 0.2) }, areaAngle: 30);
        Assert.Equal(0, aligned[0].Theta, 9);                  // the field and the area share an angle: no relative turn
    }

    [Fact]
    public void PaintingARotatedFootprintCoversTheRightCells()
    {
        var map = new CoverageMap(2, 2, 0.01);
        map.Paint(CoverageMap.Footprints(new Pose(0, 0, 90), new[] { Frame(1.0, 0.2) }, 0), 1);     // a wide frame turned upright
        double Cell(double x, double y) => map.Seconds[(int)((1 - y) / 0.01) * map.Cols + (int)((x + 1) / 0.01)];
        Assert.Equal(1, Cell(0, 0.4));            // tall now: covered high up
        Assert.Equal(0, Cell(0.4, 0));            // and not to the side
        double area = map.Seconds.Sum(v => v) * 0.01 * 0.01;
        Assert.Equal(0.2, area, 2);               // 1.0 x 0.2
    }
}
