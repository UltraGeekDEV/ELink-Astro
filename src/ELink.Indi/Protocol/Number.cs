using System.Globalization;
using System.Text;

namespace ELink.Indi.Protocol;

/// <summary>INDI numbers travel as decimals or sexagesimal ("12:30:15.5", "-5 30", "10:20"); formatted per the
/// property's printf-style format, where the non-standard "%m" means sexagesimal.</summary>
public static class IndiNumber
{
    public static double Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;

        var parts = text.Split(new[] { ':', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 3) return double.NaN;
        bool negative = parts[0].StartsWith('-');
        double total = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return double.NaN;
            total += Math.Abs(p) / scale;
            scale *= 60;
        }
        return negative ? -total : total;
    }

    /// <summary>Wire form for sending: always a plain decimal.</summary>
    public static string ToWire(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Human display per the INDI printf format (%d %i %f %g %e and %W.Fm sexagesimal).</summary>
    public static string Format(double value, string? format)
    {
        if (string.IsNullOrEmpty(format)) return value.ToString("G", CultureInfo.InvariantCulture);
        char conv = format[^1];
        try
        {
            if (conv == 'm') return Sexagesimal(value, format);
            int dot = format.IndexOf('.');
            int prec = dot >= 0 && int.TryParse(format.AsSpan(dot + 1, format.Length - dot - 2), out var p) ? p : 6;
            return conv switch
            {
                'd' or 'i' => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture),
                'f' => value.ToString("F" + prec, CultureInfo.InvariantCulture),
                'e' or 'E' => value.ToString((conv == 'e' ? "0." + new string('0', prec) + "e+00" : "0." + new string('0', prec) + "E+00"), CultureInfo.InvariantCulture),
                'g' or 'G' => value.ToString("G" + Math.Max(prec, 1), CultureInfo.InvariantCulture),
                _ => value.ToString("G", CultureInfo.InvariantCulture),
            };
        }
        catch (FormatException) { return value.ToString("G", CultureInfo.InvariantCulture); }
    }

    private static string Sexagesimal(double value, string format)
    {
        // %<width>.<fractionSpec>m : spec 3=h:mm, 5=h:mm.m, 6=h:mm:ss, 8=h:mm:ss.s, 9=h:mm:ss.ss
        int dot = format.IndexOf('.');
        int spec = dot >= 0 ? int.Parse(format.AsSpan(dot + 1, format.Length - dot - 2), CultureInfo.InvariantCulture) : 6;
        var sb = new StringBuilder();
        if (value < 0) { sb.Append('-'); value = -value; }
        double rounded = spec switch { 3 => Math.Round(value * 60) / 60, 5 => Math.Round(value * 600) / 600,
                                       6 => Math.Round(value * 3600) / 3600, 8 => Math.Round(value * 36000) / 36000,
                                       _ => Math.Round(value * 360000) / 360000 };
        int d = (int)rounded;
        double rem = (rounded - d) * 60;
        int m = (int)(rem + 1e-9);
        double sec = (rem - m) * 60;
        sb.Append(d.ToString(CultureInfo.InvariantCulture));
        sb.Append(':');
        switch (spec)
        {
            case 3: sb.Append(m.ToString("00", CultureInfo.InvariantCulture)); break;
            case 5: sb.Append((rem).ToString("00.0", CultureInfo.InvariantCulture)); break;
            case 6: sb.Append(m.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(((int)Math.Round(sec)).ToString("00", CultureInfo.InvariantCulture)); break;
            case 8: sb.Append(m.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(sec.ToString("00.0", CultureInfo.InvariantCulture)); break;
            default: sb.Append(m.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(sec.ToString("00.00", CultureInfo.InvariantCulture)); break;
        }
        return sb.ToString();
    }
}
