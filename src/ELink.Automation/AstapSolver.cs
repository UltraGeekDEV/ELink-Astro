using System.Diagnostics;
using System.Globalization;
using ELink.Imaging;

namespace ELink.Automation;

/// <summary>Plate solving with ASTAP's command-line solver (<c>astap_cli</c>, or the <c>astap</c> program; with one of
/// its star databases, e.g. D50, beside it or in <see cref="Database"/>). Fast when it knows roughly where to look;
/// it reads the field height from the hint scale. Set ELINK_ASTAP to point at a specific one.</summary>
public sealed class AstapSolver : IPlateSolver
{
    public string Name => "ASTAP";
    public string Executable { get; }
    /// <summary>The star database folder; empty = ASTAP's own default.</summary>
    public string Database { get; set; } = "";
    public int Downsample { get; set; } = 0;   // 0 = ASTAP decides

    public AstapSolver(string executable) { Executable = executable; }

    /// <summary>ELINK_ASTAP, then ~/.local/astap, /opt/astap, then astap_cli / astap on the PATH.</summary>
    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("ELINK_ASTAP");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { Path.Combine(home, ".local", "astap"), "/opt/astap" }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'));
        foreach (var d in dirs)
            foreach (var exe in new[] { "astap_cli", "astap" })
                if (d != "" && File.Exists(Path.Combine(d, exe))) return Path.Combine(d, exe);
        return null;
    }

    public async Task<SolveOutcome> SolveAsync(byte[] fits, double hintRaHours, double hintDecDegrees, double hintRadiusDegrees,
        double scaleLow, double scaleHigh, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string dir = Path.Combine(Path.GetTempPath(), "elink-astap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var header = PlateSolver.ReadHeader(fits);
            int width = Int(header, "NAXIS1"), height = Int(header, "NAXIS2");
            string image = Path.Combine(dir, "frame.fits");
            await File.WriteAllBytesAsync(image, fits, ct);
            var psi = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
            void A(params string[] xs) { foreach (var x in xs) psi.ArgumentList.Add(x); }
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            A("-f", image, "-wcs");
            bool hinted = !double.IsNaN(hintRaHours) && !double.IsNaN(hintDecDegrees);
            if (hinted) A("-ra", F(hintRaHours), "-spd", F(hintDecDegrees + 90), "-r", F(Math.Clamp(hintRadiusDegrees, 0.1, 180)));
            else A("-r", "180");
            // the field height in degrees: from the middle of the scale range (0 = ASTAP works it out from the header)
            double fov = scaleLow > 0 && scaleHigh >= scaleLow && height > 0 ? (scaleLow + scaleHigh) / 2 * height / 3600 : 0;
            A("-fov", F(fov));
            if (Downsample > 0) A("-z", Downsample.ToString(CultureInfo.InvariantCulture));
            if (Database != "") A("-d", Database);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start ASTAP");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout + TimeSpan.FromSeconds(5));
            var stdout = p.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = p.StandardError.ReadToEndAsync(linked.Token);
            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return Fail(sw, ct.IsCancellationRequested ? "cancelled" : "solving timed out"); }
            string output = (await stdout + "\n" + await stderr).Trim();

            // ASTAP's answer: frame.ini (PLTSOLVD, the solution, ERROR/WARNING) and, when solved, frame.wcs (a FITS header)
            var ini = ReadIni(Path.Combine(dir, "frame.ini"));
            if (!ini.TryGetValue("PLTSOLVD", out var solved) || !solved.StartsWith('T'))
            {
                string why = ini.GetValueOrDefault("ERROR") ?? ini.GetValueOrDefault("WARNING") ?? (output != "" ? output.Split('\n')[^1] : "");
                return Fail(sw, "no solution" + (why != "" ? $" ({why.Trim()})" : " (not enough stars, or the hints/scale are wrong)"));
            }
            Dictionary<string, string> sol = ini;
            string wcsFile = Path.Combine(dir, "frame.wcs");
            if (File.Exists(wcsFile))
            {
                try { sol = PlateSolver.ReadHeader(await File.ReadAllBytesAsync(wcsFile, ct)); } catch (FormatException) { }
            }
            sol.TryAdd("CTYPE1", "RA---TAN"); sol.TryAdd("CTYPE2", "DEC--TAN");
            if (TanWcs.FromHeader(sol) is not { } wcs) return Fail(sw, "ASTAP said solved but gave no usable solution");
            return FromWcs(wcs, width, height, sw.Elapsed.TotalSeconds);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Centre, rotation, scale and field from a TAN solution of a width x height image.</summary>
    public static SolveOutcome FromWcs(TanWcs wcs, int width, int height, double seconds)
    {
        var (ra, dec) = wcs.PixelToSky((width - 1) / 2.0, (height - 1) / 2.0);
        // image up (+y) on the sky: (east, north) = (CD1_2, CD2_2)
        double pa = Math.Atan2(wcs.Cd12, wcs.Cd22) * 180 / Math.PI;
        pa = ((pa % 360) + 360) % 360;
        double scale = wcs.PixelScaleArcsec;
        return new SolveOutcome(true, ((ra / 15 % 24) + 24) % 24, dec, pa, scale, scale * width / 3600, scale * height / 3600, seconds, "") { Wcs = wcs };
    }

    private static int Int(Dictionary<string, string> h, string k) => h.TryGetValue(k, out var v) && int.TryParse(v, out int i) ? i : 0;

    private static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static SolveOutcome Fail(Stopwatch sw, string message) => new(false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, sw.Elapsed.TotalSeconds, message);
}
