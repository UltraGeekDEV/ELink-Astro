using System.Diagnostics;
using System.Globalization;

namespace ELink.Atlas;

/// <summary>Faint stars (to about magnitude 15) from the Guide Star Catalogue, through the <c>gsc</c> query tool that INDI's
/// CCD simulator uses (GSCDAT, usually /usr/share/GSC). Meant for small fields: one query reads the catalogue regions it needs.</summary>
public sealed class GscDeepStars
{
    private readonly string _tool, _data;

    public GscDeepStars(string tool = "gsc", string data = "/usr/share/GSC") { _tool = tool; _data = data; }

    /// <summary>True when the tool and its data are installed.</summary>
    public bool Available => Directory.Exists(_data) && (File.Exists(_tool) || FindOnPath(_tool) is not null);

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>Stars within the radius, between the magnitudes, brightest first.</summary>
    public async Task<List<CatalogStar>> QueryAsync(double raHours, double decDegrees, double radiusDegrees, double magMin, double magMax, int max, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["GSCDAT"] = _data;
        foreach (var a in new[]
        {
            "-c", (raHours * 15).ToString("0.00000", CultureInfo.InvariantCulture), decDegrees.ToString("+0.00000;-0.00000", CultureInfo.InvariantCulture),
            "-r", Math.Max(0.1, radiusDegrees * 60).ToString("0.0", CultureInfo.InvariantCulture),
            "-m", magMin.ToString("0.00", CultureInfo.InvariantCulture), magMax.ToString("0.00", CultureInfo.InvariantCulture),
            "-n", Math.Max(1, max).ToString(CultureInfo.InvariantCulture), "-s", "5",
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var stars = new List<CatalogStar>();
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith('#') || line.Length < 30) continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5) continue;
            if (!double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var raDeg) ||
                !double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) ||
                !float.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var mag)) continue;
            stars.Add(new CatalogStar(raDeg / 15, dec, mag, 0.6f, 0, ""));
        }
        await p.WaitForExitAsync(ct);
        return stars;
    }
}
