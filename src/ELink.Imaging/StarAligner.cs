namespace ELink.Imaging;

/// <summary>Finds how one frame sits on another from their stars alone (no plate solving): a shift and a small turn, found by letting every pair
/// of bright stars vote for a shift, then matching the stars and fitting the turn. Meant for frames of the same field from a tracking mount.</summary>
public static class StarAligner
{
    /// <summary>x' = Cos·x − Sin·y + Tx, y' = Sin·x + Cos·y + Ty: a position in the frame mapped into the reference.</summary>
    public readonly record struct Transform(double Cos, double Sin, double Tx, double Ty, int Matches, double RmsPixels)
    {
        public (double X, double Y) Apply(double x, double y) => (Cos * x - Sin * y + Tx, Sin * x + Cos * y + Ty);
        public double RotationDegrees => Math.Atan2(Sin, Cos) * 180 / Math.PI;
    }

    /// <summary>The transform that puts the frame's stars on the reference's, or null when they do not match.</summary>
    /// <param name="maxShift">the largest shift looked for, pixels</param>
    /// <param name="starSize">how big the stars are, pixels (HFR): soft or trailed stars have less certain centres, so the matching is looser</param>
    public static Transform? Align(IReadOnlyList<Star> reference, IReadOnlyList<Star> frame, double maxShift = 600, int minMatches = 8, double starSize = 2)
    {
        // the reference may cover more sky than the frame (another telescope): look deeper into it than into the frame
        var r = reference.OrderByDescending(s => s.Flux).Take(500).ToList();
        var f = frame.OrderByDescending(s => s.Flux).Take(120).ToList();
        if (r.Count < minMatches || f.Count < minMatches) return null;

        // every pair votes for the shift that would bring them together
        double bin = Math.Max(5, 1.5 * starSize);
        var votes = new Dictionary<(int, int), int>();
        foreach (var a in r)
            foreach (var b in f)
            {
                double dx = a.X - b.X, dy = a.Y - b.Y;
                if (Math.Abs(dx) > maxShift || Math.Abs(dy) > maxShift) continue;
                var key = ((int)Math.Floor(dx / bin), (int)Math.Floor(dy / bin));
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        if (votes.Count == 0) return null;
        // the bin with most votes, counting its neighbours (a turn smears the shift over a few bins)
        var best = votes.Keys.MaxBy(k => { int s = 0; for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++) s += votes.GetValueOrDefault((k.Item1 + i, k.Item2 + j)); return s; });
        var near = new List<(double X, double Y)>();
        double tx = 0, ty = 0; int n = 0;
        foreach (var a in r) foreach (var b in f)
            {
                double dx = a.X - b.X, dy = a.Y - b.Y;
                if (Math.Abs(dx / bin - best.Item1 - 0.5) <= 1.6 && Math.Abs(dy / bin - best.Item2 - 0.5) <= 1.6) { tx += dx; ty += dy; n++; }
            }
        if (n < 3) return null;
        var t = new Transform(1, 0, tx / n, ty / n, 0, double.MaxValue);

        // refine: match stars within a shrinking radius and fit the turn and shift to the matches
        foreach (double radius in new[] { Math.Max(14.0, 4 * starSize), Math.Max(8.0, 2.5 * starSize), Math.Max(4.0, 1.4 * starSize), Math.Max(3.0, 1.0 * starSize) })
        {
            var pairs = Match(reference, frame, t, radius);
            if (pairs.Count < minMatches) return null;
            t = Fit(pairs);
        }
        return t.Matches >= minMatches && t.RmsPixels < Math.Max(1.5, 0.45 * starSize) ? t : null;
    }

    private static List<(Star Ref, Star Frame)> Match(IReadOnlyList<Star> reference, IReadOnlyList<Star> frame, Transform t, double radius)
    {
        var pairs = new List<(Star, Star)>();
        var used = new HashSet<int>();
        foreach (var s in frame.OrderByDescending(s => s.Flux).Take(150))
        {
            var (x, y) = t.Apply(s.X, s.Y);
            int bestI = -1; double bestD = radius * radius;
            for (int i = 0; i < reference.Count; i++)
            {
                if (used.Contains(i)) continue;
                double dx = reference[i].X - x, dy = reference[i].Y - y, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; bestI = i; }
            }
            if (bestI >= 0) { used.Add(bestI); pairs.Add((reference[bestI], s)); }
        }
        return pairs;
    }

    /// <summary>The best rotation and shift for the pairs (the closed form for two dimensions).</summary>
    private static Transform Fit(List<(Star Ref, Star Frame)> pairs)
    {
        double mfx = pairs.Average(p => p.Frame.X), mfy = pairs.Average(p => p.Frame.Y), mrx = pairs.Average(p => p.Ref.X), mry = pairs.Average(p => p.Ref.Y);
        double a = 0, b = 0;
        foreach (var (rf, fr) in pairs)
        {
            double fx = fr.X - mfx, fy = fr.Y - mfy, rx = rf.X - mrx, ry = rf.Y - mry;
            a += fx * rx + fy * ry; b += fx * ry - fy * rx;
        }
        double norm = Math.Sqrt(a * a + b * b);
        double c = norm > 0 ? a / norm : 1, s = norm > 0 ? b / norm : 0;
        double tx = mrx - (c * mfx - s * mfy), ty = mry - (s * mfx + c * mfy);
        var t = new Transform(c, s, tx, ty, pairs.Count, 0);
        double sum = 0;
        foreach (var (rf, fr) in pairs) { var (x, y) = t.Apply(fr.X, fr.Y); sum += (x - rf.X) * (x - rf.X) + (y - rf.Y) * (y - rf.Y); }
        return t with { RmsPixels = Math.Sqrt(sum / pairs.Count) };
    }
}
