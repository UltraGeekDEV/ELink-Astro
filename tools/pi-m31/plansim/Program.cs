// Runs the coverage planner for a whole night offline, exactly as ImagingService builds it, and measures where the shots land.
using System.Globalization;
using ELink.Automation;
using ELink.Core.Astro;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
double target = double.Parse(args.Length > 0 ? args[0] : "600"), W = args.Length > 1 ? double.Parse(args[1]) : 8.97, H = args.Length > 2 ? double.Parse(args[2]) : 5.95,
    PA = args.Length > 3 ? double.Parse(args[3]) : 35, minInside = args.Length > 4 ? double.Parse(args[4]) : 0,
    exp = args.Length > 5 ? double.Parse(args[5]) : 30, perShot = exp + 6, camRot = args.Length > 6 ? double.Parse(args[6]) : 0;
bool hilbert = args.Length > 7 && args[7] == "hilbert";
var clock = System.Diagnostics.Stopwatch.StartNew();
double fw = 1.490, fh = 0.993;
Console.WriteLine("framing            fill  margin oneway | shots  hours  passes  first-data  inside  <1/4in  min/mean/max   reversals");
foreach (var (pa, cam) in new[] { (PA, camRot) })
foreach (var (fill, margin, oneway) in new[] { (0.01, 1.0, true) })
{
    double minFrame = Math.Min(fw, fh), stepover = 0.1 * minFrame, cell = Math.Max(minFrame / 12, Math.Sqrt(W * H / 40000.0));
    var map = new CoverageMap(W, H, cell);
    var frames = new[] { new FrameSpec(fw / 2, fh / 2, cam, 0, 0, 1, 0.894) };
    var p = new CoveragePlanner(map, frames, [0.0], pa, exp, target, stepover) { PassMinFill = fill, MarginShare = margin, OneWay = oneway, MinInside = minInside, Hilbert = hilbert };
    p.Plan();
    int shots = 0, inPass = 0, slivers = 0, firstFull = -1, reversals = 0; double inside = 0; Pose? last = null; (double, double) dir = (0, 0);
    while (p.Next() is { } pose && shots < 20000)
    {
        shots++; if (p.InPasses) inPass++; if (firstFull < 0 && map.Min() > 0) firstFull = shots;
        if (last is { } l) { double dx = pose.X - l.X, dy = pose.Y - l.Y, len = Math.Sqrt(dx * dx + dy * dy); if (len > 1e-9) { var d = (dx / len, dy / len); if (dir != (0, 0) && d.Item1 * dir.Item1 + d.Item2 * dir.Item2 < -0.3) reversals++; dir = d; } }
        last = pose;
        var fp = CoverageMap.Footprints(pose, frames, pa)[0];
        double c = Math.Cos(fp.Theta), s = Math.Sin(fp.Theta); int n = 0, k = 0;
        for (int i = 0; i < 24; i++) for (int j = 0; j < 16; j++)
        {
            double lx = (-1 + (i + 0.5) / 12) * fp.HalfWidth, ly = (-1 + (j + 0.5) / 8) * fp.HalfHeight;
            double x = fp.Cx + lx * c + ly * s, y = fp.Cy - lx * s + ly * c; n++;
            if (Math.Abs(x) <= W / 2 && Math.Abs(y) <= H / 2) k++;
        }
        double f = (double)k / n; inside += f; if (f < 0.25) slivers++;
    }
    if (firstFull < 0 && map.Min() > 0) firstFull = shots;
    Console.WriteLine($"PA {pa,3} cam {cam,3}   {fill,4:0.00}  {margin,4:0.0}   {(oneway ? "yes" : "no "),3}   | {shots,5}  {shots * perShot / 3600,5:0.0}  {p.PassCount,4}     {firstFull * perShot / 3600,5:0.0} h    {100 * inside / shots,4:0}%   {100.0 * slivers / shots,4:0}%   {map.Min(),3:0}/{map.Mean(),3:0}/{map.Max(),3:0}   {reversals,5}  passes {inPass} top-up {shots - inPass}, pass hop {p.PassHopX:0.000}x{p.PassHopY:0.000}");
    { var sec = map.Seconds; int n90 = sec.Count(v => v < 0.9 * target), n50 = sec.Count(v => v < 0.5 * target);
      Console.WriteLine($"  area short of the depth: {100.0 * n90 / sec.Length:0.0}% below 90%, {100.0 * n50 / sec.Length:0.0}% below half; mean {map.Mean() / target:0.00}x the depth; exposure inside the area {map.Mean() * W * H / (shots * exp * fw * fh) * 100:0}%"); }
}

Console.WriteLine($"planner dry run: {clock.ElapsedMilliseconds} ms");
if (args.Length > 1) return;
// a restart a third of the way into the first pass: where does the next shot go?
{
    double minFrame = Math.Min(fw, fh), stepover = 0.1 * minFrame, cell = Math.Max(minFrame / 12, Math.Sqrt(W * H / 40000.0));
    var map = new CoverageMap(W, H, cell);
    var frames = new[] { new FrameSpec(fw / 2, fh / 2, 270, 0, 0, 1, 0.894) };
    var p = new CoveragePlanner(map, frames, [0.0], 35, exp, target, stepover) { PassMinFill = 0.25, MarginShare = 0.6 };
    p.Plan(); Pose? last = null; int n = 0;
    while (n < 25 && p.Next() is { } q) { last = q; n++; }
    var again = new CoveragePlanner(map, frames, [0.0], 35, exp, target, stepover) { PassMinFill = 0.25, MarginShare = 0.6 };
    again.Plan();
    var next = again.Next();
    Console.WriteLine($"restart after {n} shots (last at {last?.X:0.00},{last?.Y:0.00}): next shot at {next?.X:0.00},{next?.Y:0.00} (the area is {W} x {H}, top is y = {H / 2:0.00})");
}
Console.WriteLine($"ideal (no overlap, no edge): {W * H * target / (fw * fh * exp):0} shots = {W * H * target / (fw * fh * exp) * perShot / 3600:0.0} h");
