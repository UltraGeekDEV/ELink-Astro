using System.Globalization;
using System.Text.RegularExpressions;

namespace ELink.Core.Astro;

/// <summary>Hours/degrees as h:m:s text, both ways. Parsing is tolerant: "12:30:15", "12 30 15", "12h30m15s",
/// "-5°30'", or a plain decimal.</summary>
public static class Sexagesimal
{
    public static string Format(double value, int secondDecimals = 1)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "--";
        string sign = value < 0 ? "-" : "";
        double v = Math.Abs(value);
        double scale = Math.Pow(10, secondDecimals);
        double totalSeconds = Math.Round(v * 3600 * scale) / scale;
        int h = (int)(totalSeconds / 3600);
        int m = (int)((totalSeconds - h * 3600) / 60);
        double s = totalSeconds - h * 3600 - m * 60;
        string sec = secondDecimals > 0 ? s.ToString("00." + new string('0', secondDecimals), CultureInfo.InvariantCulture)
                                        : ((int)Math.Round(s)).ToString("00", CultureInfo.InvariantCulture);
        return $"{sign}{h:00}:{m:00}:{sec}";
    }

    private static readonly Regex Separators = new(@"[:hHdD°mM'’′sS""″\s]+", RegexOptions.Compiled);

    public static bool TryParse(string? text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        bool negative = text.StartsWith('-');
        var parts = Separators.Split(text.TrimStart('-', '+')).Where(p => p.Length > 0).ToArray();
        if (parts.Length is 0 or > 3) return false;
        double total = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0) return false;
            total += p / scale;
            scale *= 60;
        }
        value = negative ? -total : total;
        return true;
    }
}
