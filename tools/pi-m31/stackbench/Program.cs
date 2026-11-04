using ELink.Imaging;
// the M 45 request: 8.96 x 5.95 deg at 5.37"/px around the Pleiades; frames 3000 x 2000 super pixels at 1.788"/px
double ra = 3.79 * 15, dec = 24.1167;
var grid = TanWcs.Centered(ra, dec, double.Parse(args.Length > 0 ? args[0] : "125"), 5.37, 6019, 3992);
var st = new LiveStacker(grid, 6019, 3992);
var rnd = new Random(1);
var data = new float[3 * 3000 * 2000]; for (int i = 0; i < data.Length; i++) data[i] = 1000 + rnd.Next(100);
foreach (var (dx, dy) in new[] { (0.0, 0.0), (-4.0, 2.0), (4.3, -2.7), (-4.4, -2.9) })
{
    var f = TanWcs.Centered(ra + dx / Math.Cos(dec * Math.PI / 180), dec + dy, 90, 1.788, 3000, 2000);
    try { var r = st.Add(data, 3000, 2000, 3, f, new float[] { 1000, 1000, 1000 }); Console.WriteLine($"({dx},{dy}): {r.Added} {r.Message} bin {r.BinFactor} k {r.Subsamples}"); }
    catch (Exception e) { Console.WriteLine($"({dx},{dy}): {e.GetType().Name}: {(e is AggregateException a ? a.InnerExceptions[0].ToString() : e.ToString()).Split('\n').Take(4).Aggregate((p, q) => p + "\n" + q)}"); }
}
