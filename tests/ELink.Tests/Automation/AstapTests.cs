using System.Globalization;
using ELink.Automation;
using ELink.Contracts.Automation;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.Tests.Automation;

/// <summary>ASTAP behind the PlateSolve service, with a stand-in astap_cli that answers the way the real one does
/// (frame.ini with PLTSOLVD and the solution, frame.wcs as a FITS header) and records how it was called.</summary>
public class AstapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elink-astap-test-" + Guid.NewGuid().ToString("N"));
    public AstapTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const int W = 1280, H = 1024;
    private static readonly TanWcs Truth = TanWcs.Centered(83.8, -5.4, 33, 2.68, W, H);

    /// <summary>A stand-in astap_cli: solved (with <paramref name="wcs"/>), or not (with an error).</summary>
    private string FakeAstap(TanWcs? wcs, bool writeWcsFile = true)
    {
        string log = Path.Combine(_dir, "args.txt");
        var ini = new List<string>();
        string wcsCards = "";
        if (wcs is null) ini.AddRange(["PLTSOLVD=F", "ERROR=Not enough stars."]);
        else
        {
            ini.Add("PLTSOLVD=T");
            foreach (var (k, v) in wcs.Cards()) if (!k.StartsWith("CTYPE")) ini.Add($"{k}={v}");
            if (writeWcsFile)
                wcsCards = string.Concat(new[] { ("SIMPLE", "T"), ("NAXIS", "0") }.Concat(wcs.Cards()).Select(c => (c.Item1.PadRight(8) + "= " + c.Item2.PadLeft(20)).PadRight(80))) + "END".PadRight(80);
        }
        string script = Path.Combine(_dir, "astap_cli");
        File.WriteAllText(script, $$"""
            #!/usr/bin/env python3
            import sys, os
            args = sys.argv[1:]
            open({{Py(log)}}, "w").write("\n".join(args))
            image = args[args.index("-f") + 1]
            base = os.path.splitext(image)[0]
            open(base + ".ini", "w").write({{Py(string.Join("\n", ini) + "\n")}})
            wcs = {{Py(wcsCards)}}
            if wcs:
                open(base + ".wcs", "w").write(wcs)
            print("Solving done")
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static string Py(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'";

    private string[] Args() => File.ReadAllLines(Path.Combine(_dir, "args.txt"));
    private static double Arg(string[] args, string name) => double.Parse(args[Array.IndexOf(args, name) + 1], CultureInfo.InvariantCulture);

    private static byte[] Frame() => FitsImage.Write16(W, H, new ushort[W * H]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // only frame.ini
    public async Task ReadsTheSolutionAndPassesTheHints(bool wcsFile)
    {
        var solver = new AstapSolver(FakeAstap(Truth, wcsFile));
        var o = await solver.SolveAsync(Frame(), 5.6, -5.0, 3, 2.5, 2.9, TimeSpan.FromSeconds(30));
        Assert.True(o.Solved, o.Message);
        Assert.Equal(83.8 / 15, o.RaHours, 4);
        Assert.Equal(-5.4, o.DecDegrees, 4);
        Assert.Equal(33, o.PositionAngle, 3);
        Assert.Equal(2.68, o.PixelScale, 3);
        Assert.Equal(2.68 * W / 3600, o.FieldWidthDegrees, 4);
        Assert.NotNull(o.Wcs);

        var args = Args();
        Assert.Contains("-wcs", args);
        Assert.Equal(5.6, Arg(args, "-ra"), 6);          // hours
        Assert.Equal(85.0, Arg(args, "-spd"), 6);        // south pole distance: dec + 90
        Assert.Equal(3, Arg(args, "-r"), 6);
        Assert.Equal(2.7 * H / 3600, Arg(args, "-fov"), 4);   // the field height from the middle of the scale range
    }

    [Fact]
    public async Task BlindAndFailedSolves()
    {
        var solver = new AstapSolver(FakeAstap(null));
        var o = await solver.SolveAsync(Frame(), double.NaN, double.NaN, 0, 0, 0, TimeSpan.FromSeconds(30));
        Assert.False(o.Solved);
        Assert.Contains("Not enough stars", o.Message);
        var args = Args();
        Assert.Equal(180, Arg(args, "-r"));             // the whole sky
        Assert.DoesNotContain("-ra", args);
        Assert.Equal(0, Arg(args, "-fov"));              // let ASTAP work it out
    }

    [Fact]
    public void AnglesFromAWcsMatchTheGrid()
    {
        foreach (double pa in new[] { 0.0, 33, 90, 181, 359 })
            Assert.Equal(pa, AstapSolver.FromWcs(TanWcs.Centered(10, 20, pa, 1.5, 800, 600), 800, 600, 0).PositionAngle, 6);
    }

    /// <summary>A solver that answers as told and counts its calls.</summary>
    private sealed class Scripted(string name, bool solves) : IPlateSolver
    {
        public string Name => name;
        public int Calls;
        public Task<SolveOutcome> SolveAsync(byte[] fits, double ra, double dec, double radius, double lo, double hi, TimeSpan timeout, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(solves ? AstapSolver.FromWcs(Truth, W, H, 0.1) : new SolveOutcome(false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0.1, "no stars"));
        }
    }

    [Fact]
    public async Task TheServiceTriesASTAPFirstWithAHintAndFallsBack()
    {
        using var node = ElinkNode.Create("AS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var astap = new Scripted("ASTAP", solves: false);
        var anet = new Scripted("astrometry.net", solves: true);
        await using var svc = new PlateSolveService(node, anet, astap); await svc.StartAsync();
        async Task<SolveResult> Solve(SolveRequest r) => Assert.Single((await node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, r, TimeSpan.FromSeconds(20)))!);

        // hinted: ASTAP first, it fails, astrometry.net solves
        var r = await Solve(new SolveRequest { Image = new ELink.Contracts.RawBytes(Frame()), HintRaHours = 5.6, HintDecDegrees = -5 });
        Assert.True(r.Solved.Value, r.Message.Text);
        Assert.Equal("astrometry.net", r.Solver.Text);
        Assert.Equal((1, 1), (astap.Calls, anet.Calls));
        Assert.True(r.HasWcs.Value);
        // blind: astrometry.net first, and it solves
        r = await Solve(new SolveRequest { Image = new ELink.Contracts.RawBytes(Frame()) });
        Assert.Equal((1, 2), (astap.Calls, anet.Calls));
        // asked for ASTAP only: its failure is the answer
        r = await Solve(new SolveRequest { Image = new ELink.Contracts.RawBytes(Frame()), Solver = "astap" });
        Assert.False(r.Solved.Value);
        Assert.Equal("ASTAP", r.Solver.Text);
        Assert.Equal((2, 2), (astap.Calls, anet.Calls));
        Assert.Equal("astrometry.net,ASTAP", Assert.Single((await node.CallFunctionAsync<Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid, EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString>(SolveIds.Solvers, Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid.Void))!).Text);
    }
}
