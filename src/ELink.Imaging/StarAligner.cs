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

/// <summary>A shift, a turn and a change of scale: x' = A·x − B·y + Tx, y' = B·x + A·y + Ty, where A = scale·cos, B = scale·sin.</summary>
public readonly record struct Similarity(double A, double B, double Tx, double Ty, int Matches, double RmsPixels)
{
    public double Scale => Math.Sqrt(A * A + B * B);
    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;
    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + Tx, B * x + A * y + Ty);
    public static Similarity From(StarAligner.Transform t, double scale = 1) => new(t.Cos * scale, t.Sin * scale, t.Tx, t.Ty, t.Matches, t.RmsPixels);
}

/// <summary>Finds how a frame sits on another with any turn and any change of scale (another telescope, another camera angle) from the stars
/// alone: triangles of neighbouring stars have shapes that do not depend on turn or scale, so matching shapes gives candidate placements,
/// which are voted on and then checked against all the stars.</summary>
public static class TriangleAligner
{
    private readonly record struct Tri(double U, double V, int A, int B, int C, double Longest);

    /// <summary>The placement of the frame's stars on the reference's, or null.</summary>
    /// <param name="starSize">the stars' size in pixels (their centres are less certain when they are big)</param>
    public static Similarity? Align(IReadOnlyList<Star> reference, IReadOnlyList<Star> frame, int minMatches = 8, double starSize = 2)
    {
        var r = reference.OrderByDescending(s => s.Flux).Take(260).ToList();
        var f = frame.OrderByDescending(s => s.Flux).Take(110).ToList();
        if (r.Count < minMatches || f.Count < minMatches) return null;
        var rt = Triangles(r); var ft = Triangles(f);
        if (rt.Count == 0 || ft.Count == 0) return null;

        // reference triangles by their shape, in cells of 0.02 over (u, v)
        const double cell = 0.02;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < rt.Count; i++)
        {
            var key = ((int)(rt[i].U / cell), (int)(rt[i].V / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(i);
        }
        // each pair of triangles of the same shape is a candidate placement: vote on (scale, turn, shift)
        var votes = new Dictionary<(int, int, int, int), List<Similarity>>();
        foreach (var t in ft)
        {
            double tol = 0.012 + 1.2 * Math.Max(1, starSize) / Math.Max(t.Longest, 1);
            int cx = (int)(t.U / cell), cy = (int)(t.V / cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var list))
                        foreach (int i in list)
                        {
                            var o = rt[i];
                            if (Math.Abs(o.U - t.U) > tol || Math.Abs(o.V - t.V) > tol) continue;
                            var s = Fit([(f[t.A], r[o.A]), (f[t.B], r[o.B]), (f[t.C], r[o.C])]);
                            if (s.Scale < 0.02 || s.Scale > 50) continue;
                            var key = ((int)Math.Round(Math.Log(s.Scale) / 0.03), (int)Math.Round(s.RotationDegrees / 1.0), (int)Math.Round(s.Tx / 40), (int)Math.Round(s.Ty / 40));
                            if (!votes.TryGetValue(key, out var v)) votes[key] = v = new List<Similarity>();
                            v.Add(s);
                        }
        }
        if (votes.Count == 0) return null;

        // the best-voted placements are checked against all the stars
        var rGrid = Index(r, 10);
        Similarity? best = null; int bestCount = 0;
        foreach (var group in votes.OrderByDescending(kv => kv.Value.Count).Take(12))
        {
            var cand = Median(group.Value);
            int count = Count(f, rGrid, cand, Math.Max(3.0, 1.2 * starSize));
            if (count > bestCount) { bestCount = count; best = cand; }
        }
        if (best is not { } s0 || bestCount < minMatches) return null;

        // refine on the stars that match, with the radius closing in
        var s1 = s0;
        foreach (double radius in new[] { Math.Max(6.0, 2.5 * starSize), Math.Max(4.0, 1.6 * starSize), Math.Max(3.0, 1.1 * starSize) })
        {
            var pairs = MatchPairs(reference, frame, s1, radius);
            if (pairs.Count < minMatches) return null;
            s1 = Fit(pairs);
        }
        return s1.Matches >= minMatches && s1.RmsPixels < Math.Max(1.5, 0.45 * starSize) ? s1 : null;
    }

    private static List<Tri> Triangles(List<Star> s)
    {
        var result = new List<Tri>(); var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < s.Count; i++)
        {
            var near = Enumerable.Range(0, s.Count).Where(j => j != i).OrderBy(j => D2(s[i], s[j])).Take(6).ToList();
            for (int a = 0; a < near.Count; a++)
                for (int b = a + 1; b < near.Count; b++)
                {
                    var idx = new[] { i, near[a], near[b] }; Array.Sort(idx);
                    if (!seen.Add((idx[0], idx[1], idx[2]))) continue;
                    // vertices ordered by the length of the side opposite them: the shape is then the same however it is turned or scaled
                    var v = new[] { idx[0], idx[1], idx[2] };
                    double Opp(int k) => Math.Sqrt(D2(s[v[(k + 1) % 3]], s[v[(k + 2) % 3]]));
                    var order = Enumerable.Range(0, 3).OrderBy(Opp).ToArray();
                    double l0 = Opp(order[0]), l1 = Opp(order[1]), l2 = Opp(order[2]);
                    if (l2 < 8 || l0 / l2 < 0.12 || l1 - l0 < 0.03 * l2 || l2 - l1 < 0.03 * l2) continue;   // too small, too thin, or two sides too alike to tell the corners apart
                    result.Add(new Tri(l1 / l2, l0 / l2, v[order[0]], v[order[1]], v[order[2]], l2));
                }
        }
        return result;
    }

    private static double D2(Star a, Star b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    /// <summary>Least squares for x' = c·x + t with c a complex number (turn and scale).</summary>
    private static Similarity Fit(IReadOnlyList<(Star Frame, Star Ref)> pairs)
    {
        double mfx = pairs.Average(p => p.Frame.X), mfy = pairs.Average(p => p.Frame.Y), mrx = pairs.Average(p => p.Ref.X), mry = pairs.Average(p => p.Ref.Y);
        double num_re = 0, num_im = 0, den = 0;
        foreach (var (fr, rf) in pairs)
        {
            double zx = fr.X - mfx, zy = fr.Y - mfy, wx = rf.X - mrx, wy = rf.Y - mry;
            num_re += wx * zx + wy * zy; num_im += wy * zx - wx * zy; den += zx * zx + zy * zy;
        }
        double a = den > 0 ? num_re / den : 1, b = den > 0 ? num_im / den : 0;
        double tx = mrx - (a * mfx - b * mfy), ty = mry - (b * mfx + a * mfy);
        double sum = 0;
        foreach (var (fr, rf) in pairs) { double x = a * fr.X - b * fr.Y + tx, y = b * fr.X + a * fr.Y + ty; sum += (x - rf.X) * (x - rf.X) + (y - rf.Y) * (y - rf.Y); }
        return new Similarity(a, b, tx, ty, pairs.Count, Math.Sqrt(sum / pairs.Count));
    }

    private static Similarity Median(List<Similarity> list)
    {
        double M(Func<Similarity, double> g) { var v = list.Select(g).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
        return new Similarity(M(s => s.A), M(s => s.B), M(s => s.Tx), M(s => s.Ty), 0, 0);
    }

    private static Dictionary<(int, int), List<Star>> Index(List<Star> stars, double cell)
    {
        var g = new Dictionary<(int, int), List<Star>>();
        foreach (var s in stars) { var k = ((int)Math.Floor(s.X / cell), (int)Math.Floor(s.Y / cell)); if (!g.TryGetValue(k, out var l)) g[k] = l = new List<Star>(); l.Add(s); }
        return g;
    }

    private static int Count(List<Star> frame, Dictionary<(int, int), List<Star>> refGrid, Similarity s, double radius)
    {
        int n = 0;
        foreach (var st in frame.Take(80))
        {
            var (x, y) = s.Apply(st.X, st.Y);
            int cx = (int)Math.Floor(x / 10), cy = (int)Math.Floor(y / 10), reach = (int)Math.Ceiling(radius / 10);
            bool hit = false;
            for (int dx = -reach; dx <= reach && !hit; dx++)
                for (int dy = -reach; dy <= reach && !hit; dy++)
                    if (refGrid.TryGetValue((cx + dx, cy + dy), out var l)) foreach (var o in l) if ((o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y) <= radius * radius) { hit = true; break; }
            if (hit) n++;
        }
        return n;
    }

    private static List<(Star Frame, Star Ref)> MatchPairs(IReadOnlyList<Star> reference, IReadOnlyList<Star> frame, Similarity s, double radius)
    {
        var pairs = new List<(Star, Star)>(); var used = new HashSet<int>();
        foreach (var st in frame.OrderByDescending(x => x.Flux).Take(200))
        {
            var (x, y) = s.Apply(st.X, st.Y);
            int best = -1; double bestD = radius * radius;
            for (int i = 0; i < reference.Count; i++)
            {
                if (used.Contains(i)) continue;
                double d = (reference[i].X - x) * (reference[i].X - x) + (reference[i].Y - y) * (reference[i].Y - y);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) { used.Add(best); pairs.Add((st, reference[best])); }
        }
        return pairs;
    }
}
