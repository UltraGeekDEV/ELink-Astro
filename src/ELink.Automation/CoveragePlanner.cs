using ELink.Core.Astro;
namespace ELink.Automation;

/// <summary>Decides where the scope shoots next, so that the area slowly fills in with exposure time.
/// <para><b>Passes.</b> The area is swept in serpentine raster passes over a lattice of poses. The lattice hop is chosen so
/// that one pass deposits a uniform slice of the target depth (every cell collects <c>exposure × Σ frame areas / hop²</c>
/// per pass whatever the frames' shapes and rotations), and the number of passes so that they add up to the target. A
/// deep target therefore means several light passes with small stepovers, not a pile of shots on one spot. Successive
/// lattices are shifted against each other, and passes cycle through the allowed field rotations.</para>
/// <para><b>Top-up.</b> What the passes leave short (mostly the edges) is finished greedily: the next pose is the one
/// whose footprints fill the most deficit while exposing the fewest finished spots, reached by a small hop when
/// possible and by a jump only when somewhere else is clearly better.</para></summary>
public sealed class CoveragePlanner
{
    /// <summary>The map the planner paints as it plans (so visits queued ahead of the executor already count).</summary>
    public CoverageMap Map { get; }
    /// <summary>The frames that feed this planner's layer: where they land is what it plans.</summary>
    public IReadOnlyList<FrameSpec> Frames { get; }
    /// <summary>Every frame a shot takes (the cameras that feed other layers too): all of them are painted into the map.</summary>
    public IReadOnlyList<FrameSpec> PaintFrames { get; set; }
    /// <summary>The layer of the map this planner fills.</summary>
    public int Layer { get; }
    public IReadOnlyList<double> Rotations { get; }
    public double AreaAngle { get; }
    public double ExposureSeconds { get; }
    public double TargetSeconds { get; set; }
    public double Stepover { get; set; }
    /// <summary>How much exposing a spot that already has its target counts against a pose (0 = not at all).</summary>
    public double OverTargetPenalty { get; set; } = 1.0;
    /// <summary>A far pose must be this many times better (after a travel discount) than the best nearby one to be worth a jump.</summary>
    public double JumpAdvantage { get; set; } = 1.5;
    /// <summary>Turning the rotator costs time: a top-up pose at another field angle must score this many times better (as a factor below 1: 0.5 = twice as good).</summary>
    public double RotationMalus { get; set; } = 0.4;
    /// <summary>Let the top-up turn the rotator too (default: only the passes do).</summary>
    public bool TopUpMayRotate { get; set; }
    /// <summary>The widest lattice hop a pass may use (degrees; default ¾ of the smallest frame side, which still covers without
    /// gaps at any turn of the frame). It decides how thin a pass is: a deep image is many thin passes, so every spot has some
    /// data early and an interrupted night leaves an even image, not a finished top and an empty bottom.</summary>
    public double PassHopMax { get; set; }
    /// <summary>About how long one pass should take (seconds, counting a fifth on top of the exposures for the hops): a long
    /// image is cut into passes of about this length, as thin as <see cref="PassHopMax"/> allows.</summary>
    public double PassSeconds { get; set; } = 3600;
    /// <summary>Every row of a pass runs the same way and every pass in the same order: each short hop arrives from the same
    /// side, so an unguided mount's backlash is the same for all of them (only the start of a row comes from the other side).</summary>
    public bool OneWay { get; set; } = true;
    /// <summary>A lattice pose is shot only if it fills at least this share of a whole frame's worth of what is missing; the
    /// edges that leaves short are finished by the top-up, which places frames to fill them.</summary>
    public double PassMinFill { get; set; } = 0.01;
    /// <summary>How far beyond the area the pass lattice reaches, as a share of a frame's half diagonal (1 = up to where a
    /// frame just touches the area).</summary>
    public double MarginShare { get; set; } = 1.0;
    public Pose? Last { get; private set; }
    /// <summary>When every spot has the target: raise it by <see cref="KeepGoingStep"/> and carry on, evenly, until stopped.</summary>
    public bool KeepGoing { get; set; }
    /// <summary>How much deeper each time the target is reached while keeping going (seconds; 0 = about an hour of shots).</summary>
    public double KeepGoingStep { get; set; }
    /// <summary>The depth the pass in progress brings every spot up to: passes rise evenly from the thinnest spot to the target,
    /// so a pass skips what is already that deep (after a restart it picks up where the image is thinnest, not at the top).</summary>
    public double PassLevel { get; private set; } = double.NaN;

    /// <summary>The lattice hop the passes use (never smaller than <see cref="Stepover"/>); NaN in top-up mode.</summary>
    public double PassHop { get; private set; } = double.NaN;
    /// <summary>The hops actually used by the pass in progress (they differ when the lattice is aligned to a rectangular frame).</summary>
    public double PassHopX { get; private set; } = double.NaN;
    public double PassHopY { get; private set; } = double.NaN;
    public int PassCount { get; private set; }
    public int CurrentPass { get; private set; }
    /// <summary>True while the raster passes are running (false once the top-up has taken over).</summary>
    public bool InPasses => _lattice is not null;

    private readonly double _minFrame;
    private readonly double _frameArea;
    private readonly double _reach;    // a frame's half diagonal plus its offset: beyond this from a pose, the frame cannot reach
    private double _margin => _reach * MarginShare;
    private IEnumerator<Pose>? _lattice;
    private int _passIndex;
    private double _base;
    private readonly double _fullGain;
    private (double X, double Y) _heading = (0, 0);

    public CoveragePlanner(CoverageMap map, IReadOnlyList<FrameSpec> frames, IReadOnlyList<double> rotations, double areaAngle,
        double exposureSeconds, double targetSeconds, double stepover, int layer = 0)
    {
        Map = map; Frames = frames; PaintFrames = frames; Layer = layer; Rotations = rotations.Count > 0 ? rotations : new[] { 0.0 };
        AreaAngle = areaAngle; ExposureSeconds = exposureSeconds; TargetSeconds = targetSeconds; Stepover = stepover;
        _minFrame = frames.Min(f => 2 * Math.Min(f.HalfWidth, f.HalfHeight));
        _fullGain = frames.Sum(f => 4 * f.HalfWidth * f.HalfHeight / (map.CellWidth * map.CellHeight));
        _frameArea = frames.Sum(f => 4 * f.HalfWidth * f.HalfHeight);
        // a cell at the very edge is covered by poses up to a frame's reach outside the area: that is the margin the lattice needs
        _reach = frames.Max(f => Math.Sqrt(f.HalfWidth * f.HalfWidth + f.HalfHeight * f.HalfHeight) + Math.Sqrt(f.OffsetEast * f.OffsetEast + f.OffsetNorth * f.OffsetNorth));
        PassHopMax = 0.75 * _minFrame;
        Plan();
    }

    /// <summary>True once every spot of the area has received the target (never in endless mode).</summary>
    public bool Done => TargetSeconds > 0 && Map.Min(Layer) >= TargetSeconds - 1e-6;

    private double Weight(float seconds)
    {
        double k = 1 + seconds / ExposureSeconds;
        return 1 / (k * k);                                 // endless mode: keep the coverage level rising evenly
    }

    /// <summary>How good a shot at this pose would be, and (D) how much deficit it fills. With a target the score is
    /// D² / (D + penalty × waste): fills what is missing, pays for re-exposing finished spots, and is never negative.</summary>
    private (double Score, double Deficit) Evaluate(Pose p, double level = double.NaN)
    {
        var fp = CoverageMap.Footprints(p, Frames, AreaAngle);
        if (TargetSeconds <= 0) { double g = Map.Gain(fp, Weight, Layer); return (g, g); }
        Map.DeficitAndWaste(fp, double.IsNaN(level) ? TargetSeconds : level, out double d, out int w, Layer);
        return d <= 0 ? (0, 0) : (d * d / (d + OverTargetPenalty * w), d);
    }

    /// <summary>Poses may hang over the edge of the area (what falls outside costs nothing), up to the lattice margin.</summary>
    private bool InArea(double x, double y) => Math.Abs(x) <= Map.FovWidth / 2 + _reach && Math.Abs(y) <= Map.FovHeight / 2 + _reach;

    /// <summary>(Re)computes the passes from the target that is still missing. Called at the start and after a retarget.</summary>
    /// <summary>For an area that fits in one frame: no raster passes, every shot centred on it.</summary>
    public bool TopUpOnly { get; set; }

    public void Plan()
    {
        _lattice?.Dispose(); _lattice = null; PassHop = double.NaN; PassCount = 0;
        if (TopUpOnly && TargetSeconds > 0) return;
        double hop0 = Math.Max(Stepover, 1e-6);
        double perPassAtHop0 = ExposureSeconds * _frameArea / (hop0 * hop0);          // depth one pass deposits at the requested stepover
        if (TargetSeconds <= 0) { PassHop = hop0; PassCount = 0; _passIndex = 0; _lattice = Passes(true).GetEnumerator(); return; }   // endless
        double missing = TargetSeconds - Map.Mean(Layer);
        if (missing <= 0) return;
        // passes of about PassSeconds each, but none thinner than one at the widest gap-free hop and none deeper than the
        // stepover allows; at least one per allowed field rotation: the rotator turns between passes, never within one
        double widest = Math.Max(hop0, PassHopMax > 0 ? PassHopMax : hop0);
        double perPassAtWidest = ExposureSeconds * _frameArea / (widest * widest);
        double shots = missing * Map.FovWidth * Map.FovHeight / (ExposureSeconds * _frameArea);
        int byTime = PassSeconds > 0 ? (int)Math.Ceiling(shots * ExposureSeconds * 1.2 / PassSeconds - 1e-9) : 1;
        int thinnest = Math.Max(1, (int)Math.Floor(missing / perPassAtWidest + 1e-9));
        int passes = Math.Max(Math.Max(1, Rotations.Count), Math.Max((int)Math.Ceiling(missing / perPassAtHop0 - 1e-9), Math.Min(byTime, thinnest)));
        PassHop = Math.Max(hop0, Math.Sqrt(ExposureSeconds * _frameArea * passes / missing));   // exactly `missing` after `passes` passes
        PassCount = passes; _passIndex = 0;
        _base = Math.Min(Map.Min(Layer), TargetSeconds);
        _lattice = Passes(false).GetEnumerator();
    }

    /// <summary>Hops for one pass. When the primary frame lies along the area's axes the hops divide it into whole numbers of
    /// steps (so every cell is covered by the same number of shots and a single pass deposits uniformly); otherwise the
    /// lattice is square with the hop given.</summary>
    private (double Hx, double Hy) PassHops(double rotation, double depthThisPass, bool endless)
    {
        double hop0 = Math.Max(Stepover, 1e-6);
        if (endless || PassCount == 0) return (PassHop, PassHop);
        var f0 = Frames[0];
        double theta = (((rotation - AreaAngle + f0.Rotation) % 180) + 180) % 180;            // frame 0's turn against the area, modulo 180
        bool aligned = theta < 0.5 || theta > 179.5, upright = Math.Abs(theta - 90) < 0.5;
        if (!aligned && !upright) return (PassHop, PassHop);
        double w0 = 2 * (upright ? f0.HalfHeight : f0.HalfWidth), h0 = 2 * (upright ? f0.HalfWidth : f0.HalfHeight);
        double want = w0 * h0 * depthThisPass / (ExposureSeconds * _frameArea);                // desired poses per frame area (kx * ky)
        int kxMax = Math.Max(1, (int)Math.Floor(w0 / hop0)), kyMax = Math.Max(1, (int)Math.Floor(h0 / hop0));
        (int kx, int ky)? best = null; double bestOver = double.MaxValue, bestSkew = double.MaxValue;
        for (int kx = 1; kx <= Math.Min(kxMax, 60); kx++)
            for (int ky = 1; ky <= Math.Min(kyMax, 60); ky++)
            {
                double prod = kx * ky;
                if (prod < want * 0.97) continue;
                double over = prod / want, skew = Math.Abs(Math.Log((w0 / kx) / (h0 / ky)));
                if (over < bestOver - 1e-9 || (Math.Abs(over - bestOver) <= 1e-9 && skew < bestSkew)) { best = (kx, ky); bestOver = over; bestSkew = skew; }
            }
        return best is { } b ? (w0 / b.kx, h0 / b.ky) : (PassHop, PassHop);
    }

    private IEnumerable<Pose> Passes(bool endless)
    {
        int total = endless ? int.MaxValue : PassCount;
        double missing = endless ? 0 : TargetSeconds - Map.Mean(Layer);
        for (int pass = 0; pass < total; pass++)
        {
            CurrentPass = pass + 1;
            PassLevel = endless ? double.NaN : pass + 1 >= PassCount ? TargetSeconds : _base + (pass + 1) * (TargetSeconds - _base) / PassCount;
            double rot = Rotations[pass % Rotations.Count];
            var (hx, hy) = PassHops(rot, endless ? 0 : missing / PassCount, endless);
            PassHopX = hx; PassHopY = hy;
            // shifted lattices: a 2D low-discrepancy offset per pass, so passes interleave instead of repeating
            double ox = ((0.5 + pass * 0.7548776662466927) % 1.0) * hx, oy = ((0.5 + pass * 0.5698402909980532) % 1.0) * hy;
            double x0 = -Map.FovWidth / 2 - _margin + ox - hx, x1 = Map.FovWidth / 2 + _margin;
            double y0 = Map.FovHeight / 2 + _margin - oy + hy, y1 = -Map.FovHeight / 2 - _margin;
            int cols = (int)Math.Floor((x1 - x0) / hx) + 2, rows = (int)Math.Floor((y0 - y1) / hy) + 2;     // one hop beyond: the lattice phase is arbitrary
            var order = new List<(int r, int c)>(rows * cols);
            for (int r = 0; r < rows; r++)
                if (OneWay || r % 2 == 0) for (int c = 0; c < cols; c++) order.Add((r, c)); else for (int c = cols - 1; c >= 0; c--) order.Add((r, c));
            if (!OneWay && pass % 2 == 1) order.Reverse();       // serpentine: every other pass runs the whole route backwards, no jump between passes
            foreach (var (r, c) in order) yield return new Pose(x0 + c * hx, y0 - r * hy, rot);
        }
    }

    /// <summary>Picks the next pose and paints it into <see cref="Map"/>. Null when the target is reached.</summary>
    public Pose? Next()
    {
        var pose = NextOnce();
        if (pose is null && KeepGoing && TargetSeconds > 0)
        {
            // deep enough everywhere: a step deeper, in fresh even passes
            double step = KeepGoingStep > 0 ? KeepGoingStep : HourOfDepth();
            TargetSeconds = Math.Max(TargetSeconds, Map.Min(Layer)) + step;
            Plan();
            pose = NextOnce();
        }
        return pose;
    }

    /// <summary>About how much depth an hour of shots gives every spot (at least one exposure).</summary>
    public double HourOfDepth() => Math.Max(ExposureSeconds, PassSeconds / 1.2 / ExposureSeconds * _frameArea * ExposureSeconds / (Map.FovWidth * Map.FovHeight));

    private Pose? NextOnce()
    {
        if (Done) return null;
        if (TopUpOnly)
        {
            // the area fits in the frame: every shot centred (the caller dithers) until it is deep enough
            var centred = new Pose(0, 0, Rotations[0]);
            if (TargetSeconds > 0 && Evaluate(centred).Deficit <= 1e-12) return null;
            Commit(centred);
            return centred;
        }
        // 1. the raster passes
        while (_lattice is not null)
        {
            if (!_lattice.MoveNext()) { _lattice.Dispose(); _lattice = null; break; }
            var pose = _lattice.Current;
            var (score, deficit) = Evaluate(pose, PassLevel);
            bool useful = TargetSeconds <= 0 ? deficit > 0 : deficit >= Math.Max(0.01, PassMinFill) * _fullGain && score > 0;   // skip poses that would mostly re-expose finished spots or hang in empty sky
            if (!useful) continue;
            Commit(pose);
            return pose;
        }
        if (TargetSeconds <= 0) return null;     // endless passes only end if the lattice is exhausted (it is not)

        // 2. top-up: finish what the passes left short
        Pose best; double bestDeficit = 0;

        if (Last is { } last)
        {
            best = last; double bestScore = -1;
            double hop = Math.Max(Stepover, 1e-9), half = hop / 2;
            foreach (double rot in TopUpAngles(last.FieldAngle))
                for (int iy = -2; iy <= 2; iy++)
                    for (int ix = -2; ix <= 2; ix++)
                    {
                        double dx = ix * half, dy = iy * half;
                        if (Math.Sqrt(dx * dx + dy * dy) > hop + 1e-9) continue;
                        double x = last.X + dx, y = last.Y + dy;
                        if (!InArea(x, y)) continue;
                        var cand = new Pose(x, y, rot);
                        var (score, deficit) = Evaluate(cand);
                        score *= Continuity(dx, dy, rot, last);
                        if (score > bestScore) { bestScore = score; best = cand; bestDeficit = deficit; }
                    }
            // jump only when somewhere else is clearly better than anything within one hop
            // (the far search is the costly part: only do it when the neighbourhood is running dry)
            if (bestScore <= 0 || bestDeficit < 0.25 * _fullGain)
            {
                var far = Global(last, out double farDeficit, out double farScore);
                if (bestScore <= 0 || farScore > JumpAdvantage * bestScore) { best = far; bestDeficit = farDeficit; }
            }
        }
        else best = Global(null, out bestDeficit, out _);

        if (bestDeficit <= 1e-12) return null;             // nothing left that a shot could improve
        Commit(best);
        return best;
    }

    private void Commit(Pose pose)
    {
        if (Last is { } l) { double dx = pose.X - l.X, dy = pose.Y - l.Y; double len = Math.Sqrt(dx * dx + dy * dy); if (len > 1e-9) _heading = (dx / len, dy / len); }
        Last = pose;
        Map.Paint(PaintFootprints(pose), ExposureSeconds);
    }

    /// <summary>What a shot at this pose lands on, in every layer it feeds.</summary>
    public Footprint[] PaintFootprints(Pose pose) => CoverageMap.Footprints(pose, PaintFrames, AreaAngle);

    /// <summary>A small bonus for continuing in the same direction (smooth sweeps), a malus for turning the rotator.</summary>
    private double Continuity(double dx, double dy, double rot, Pose last)
    {
        double bonus = 1.0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 1e-9 && (_heading.X != 0 || _heading.Y != 0))
        {
            double along = (dx * _heading.X + dy * _heading.Y) / len;
            bonus += 0.02 * along;
            if (OneWay && along < -0.3) bonus *= 0.5;      // turning back costs backlash: only when it is clearly better
        }
        if (Math.Abs(NormalizeAngle(rot - last.FieldAngle)) > 1e-6) bonus *= RotationMalus;
        return bonus;
    }

    /// <summary>The rotator stays where the last pass left it while topping up (unless allowed to move).</summary>
    private IEnumerable<double> TopUpAngles(double? current) => TopUpMayRotate || current is null ? Rotations : new[] { current.Value };

    private static double NormalizeAngle(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }

    private Pose Global(Pose? from, out double deficit, out double discountedScore)
    {
        double spacing = Math.Max(Stepover, _minFrame / 4);
        double gw = Map.FovWidth + 2 * _reach, gh = Map.FovHeight + 2 * _reach;
        int nx = Math.Max(1, (int)Math.Ceiling(gw / spacing)), ny = Math.Max(1, (int)Math.Ceiling(gh / spacing));
        Pose best = from ?? new Pose(0, 0, Rotations[0]); double bestScore = -1; deficit = 0; discountedScore = 0;
        foreach (double rot in TopUpAngles(from?.FieldAngle))
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                {
                    double x = -gw / 2 + i * gw / nx, y = gh / 2 - j * gh / ny;
                    var cand = new Pose(x, y, rot);
                    var (g, dfc) = Evaluate(cand);
                    double raw = g;
                    double dist = from is { } f ? Math.Sqrt((x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y)) : 0;
                    double score = g / (1 + 0.3 * dist / _minFrame);
                    if (from is null) score *= 1 - 1e-4 * (Math.Abs(x + Map.FovWidth / 2) + Math.Abs(y - Map.FovHeight / 2)) / (Map.FovWidth + Map.FovHeight);   // begin in the north-west corner
                    if (from is { } lf && Math.Abs(NormalizeAngle(rot - lf.FieldAngle)) > 1e-6) score *= RotationMalus;
                    if (score > bestScore) { bestScore = score; best = cand; deficit = dfc; discountedScore = score; }
                }
        return best;
    }
}
