using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ELink.Automation;

public sealed record SolveOutcome(bool Solved, double RaHours, double DecDegrees, double PositionAngle, double PixelScale,
    double FieldWidthDegrees, double FieldHeightDegrees, double Seconds, string Message)
{
    /// <summary>The full solution (TAN part of solve-field's .wcs), for registering the image pixel by pixel.</summary>
    public ELink.Imaging.TanWcs? Wcs { get; init; }
}

/// <summary>Plate solving with astrometry.net's <c>solve-field</c> (installed system-wide, or unpacked in user space; set
/// ELINK_SOLVE_FIELD to point at a specific one).</summary>
public sealed class PlateSolver
{
    public string Executable { get; }
    public int Downsample { get; set; } = 2;

    public PlateSolver(string executable) { Executable = executable; }

    /// <summary>The solve-field to use: ELINK_SOLVE_FIELD, then ~/.local/astrometry/bin/solve-field, then the PATH.</summary>
    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("ELINK_SOLVE_FIELD");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "astrometry", "bin", "solve-field");
        if (File.Exists(local)) return local;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, "solve-field")).FirstOrDefault(File.Exists);
    }

    public async Task<SolveOutcome> SolveAsync(byte[] fits, double hintRaHours, double hintDecDegrees, double hintRadiusDegrees,
        double scaleLow, double scaleHigh, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string dir = Path.Combine(Path.GetTempPath(), "elink-solve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string image = Path.Combine(dir, "frame.fits");
            await File.WriteAllBytesAsync(image, fits, ct);
            var psi = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
            void A(params string[] xs) { foreach (var x in xs) psi.ArgumentList.Add(x); }
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            A(image, "--dir", dir, "--temp-dir", dir, "--overwrite", "--no-plots", "--no-verify", "--crpix-center",
              // the line-removal and uniformisation helpers need numpy; they are refinements, not needed for a solve
              "--no-remove-lines", "--uniformize", "0",
              "--new-fits", "none", "--corr", "none", "--rdls", "none", "--match", "none", "--index-xyls", "none",
              "--downsample", Downsample.ToString(CultureInfo.InvariantCulture), "--cpulimit", ((int)Math.Max(5, timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
            if (scaleLow > 0 && scaleHigh > scaleLow) A("--scale-units", "arcsecperpix", "--scale-low", F(scaleLow), "--scale-high", F(scaleHigh));
            if (!double.IsNaN(hintRaHours) && !double.IsNaN(hintDecDegrees)) A("--ra", F(hintRaHours * 15), "--dec", F(hintDecDegrees), "--radius", F(Math.Max(0.1, hintRadiusDegrees)));

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start solve-field");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout + TimeSpan.FromSeconds(10));
            var stdout = p.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = p.StandardError.ReadToEndAsync(linked.Token);
            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return Fail(sw, ct.IsCancellationRequested ? "cancelled" : "solving timed out"); }
            string output = await stdout + "\n" + await stderr;
            var outcome = Parse(output, sw.Elapsed.TotalSeconds);
            string wcsFile = Path.Combine(dir, "frame.wcs");
            if (outcome.Solved && File.Exists(wcsFile))
            {
                try { outcome = outcome with { Wcs = ELink.Imaging.TanWcs.FromHeader(ReadHeader(await File.ReadAllBytesAsync(wcsFile, ct))) }; }
                catch (FormatException) { }
            }
            return outcome.Solved
                ? outcome
                : Fail(sw, output.Contains("did not solve", StringComparison.OrdinalIgnoreCase) || output.Contains("Did not solve") ? "no solution (not enough stars, or the hints/scale are wrong)" : FirstError(output));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The primary header of a FITS file without data (solve-field's .wcs).</summary>
    public static Dictionary<string, string> ReadHeader(byte[] bytes)
    {
        var h = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int pos = 0; pos + 80 <= bytes.Length; pos += 80)
        {
            string line = System.Text.Encoding.ASCII.GetString(bytes, pos, 80);
            string key = line[..8].TrimEnd();
            if (key == "END") return h;
            if (line[8] != '=') continue;
            string v = line[10..];
            if (v.TrimStart().StartsWith('\''))
            {
                int a = v.IndexOf('\''), b = v.IndexOf('\'', a + 1);
                v = b > a ? v[(a + 1)..b].TrimEnd() : v[(a + 1)..];
            }
            else { int slash = v.IndexOf('/'); v = (slash >= 0 ? v[..slash] : v).Trim(); }
            h[key] = v;
        }
        throw new FormatException("FITS header is not terminated");
    }

    private static SolveOutcome Fail(Stopwatch sw, string message) => new(false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, sw.Elapsed.TotalSeconds, message);

    private static string FirstError(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("Traceback"));
        return line is null ? "no solution" : "solver failed: " + line.Trim();
    }

    private static readonly Regex Center = new(@"Field center: \(RA,Dec\) = \(([-\d.]+), ([-\d.]+)\) deg", RegexOptions.Compiled);
    private static readonly Regex Size = new(@"Field size: ([\d.]+) x ([\d.]+) (arcminutes|degrees|arcseconds)", RegexOptions.Compiled);
    private static readonly Regex Rotation = new(@"Field rotation angle: up is ([-\d.]+) degrees ([EW]) of N", RegexOptions.Compiled);
    private static readonly Regex Scale = new(@"pixel scale ([\d.]+) arcsec/pix", RegexOptions.Compiled);

    /// <summary>Reads solve-field's report.</summary>
    public static SolveOutcome Parse(string output, double seconds)
    {
        var c = Center.Match(output);
        if (!c.Success) return new(false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, seconds, "no solution");
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        double ra = D(c.Groups[1].Value) / 15, dec = D(c.Groups[2].Value);
        double w = double.NaN, h = double.NaN;
        if (Size.Match(output) is { Success: true } s)
        {
            double k = s.Groups[3].Value switch { "degrees" => 1, "arcminutes" => 1 / 60.0, _ => 1 / 3600.0 };
            w = D(s.Groups[1].Value) * k; h = D(s.Groups[2].Value) * k;
        }
        double pa = double.NaN;
        if (Rotation.Match(output) is { Success: true } r) { pa = D(r.Groups[1].Value) * (r.Groups[2].Value == "E" ? 1 : -1); pa = ((pa % 360) + 360) % 360; }
        double scale = Scale.Match(output) is { Success: true } sc ? D(sc.Groups[1].Value) : double.NaN;
        return new(true, ((ra % 24) + 24) % 24, dec, pa, scale, w, h, seconds, "");
    }
}
