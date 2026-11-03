using System.Diagnostics;

namespace ELink.Automation;

/// <summary>What both solver programs share: they run at a low CPU priority (a solve never needs to beat the camera, the
/// stack or the web page to the processor; on a Raspberry Pi a solve at normal priority held capture up by 17 s), and big
/// frames are binned before solving.</summary>
internal static class Solving
{
    private static readonly string? Nice = new[] { "/usr/bin/nice", "/bin/nice" }.FirstOrDefault(File.Exists);

    public static ProcessStartInfo Start(string executable, string dir)
    {
        var psi = new ProcessStartInfo(Nice ?? executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        if (Nice is not null) { psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("15"); psi.ArgumentList.Add(executable); }
        return psi;
    }

    /// <summary>Binning that brings the frame to about 3 megapixels (at least 2, at most 8).</summary>
    public static int AutoDownsample(IReadOnlyDictionary<string, string> header)
    {
        long w = header.TryGetValue("NAXIS1", out var a) && long.TryParse(a, out var x) ? x : 0;
        long h = header.TryGetValue("NAXIS2", out var b) && long.TryParse(b, out var y) ? y : 0;
        if (w <= 0 || h <= 0) return 2;
        return Math.Clamp((int)Math.Round(Math.Sqrt(w * h / 3e6)), 2, 8);
    }
}
