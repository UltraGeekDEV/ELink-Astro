using System.Globalization;

namespace ELink.Imaging;

/// <summary>A FITS TAN (gnomonic) world coordinate system. Pixel coordinates here are 0-based over <see cref="FitsImage.Data"/>
/// (index x + y * width, in file order), so FITS pixel (1, 1) is (0, 0). Sky coordinates are J2000 degrees.</summary>
public sealed record TanWcs(double CrVal1, double CrVal2, double CrPix1, double CrPix2, double Cd11, double Cd12, double Cd21, double Cd22)
{
    private const double D2R = Math.PI / 180;

    /// <summary>Arcseconds per pixel (geometric mean of the two axes).</summary>
    public double PixelScaleArcsec => Math.Sqrt(Math.Abs(Cd11 * Cd22 - Cd12 * Cd21)) * 3600;

    /// <summary>A sky-oriented grid: <paramref name="positionAngle"/> is where the image's up (+y) points, degrees east of
    /// north; east is left of north as on the sky (not mirrored). The tangent point is the centre of the grid.</summary>
    public static TanWcs Centered(double raDeg, double decDeg, double positionAngle, double scaleArcsec, int width, int height)
    {
        double s = scaleArcsec / 3600, t = positionAngle * D2R;
        // +y = up = (east sin t, north cos t); +x = up turned a quarter toward west = (-cos t, sin t)
        return new(raDeg, decDeg, (width - 1) / 2.0, (height - 1) / 2.0, -s * Math.Cos(t), s * Math.Sin(t), s * Math.Sin(t), s * Math.Cos(t));
    }

    /// <summary>Reads CRVAL/CRPIX and CD (or CDELT + CROTA2 / PC) from a FITS header; null if it has no TAN solution.</summary>
    public static TanWcs? FromHeader(IReadOnlyDictionary<string, string> h)
    {
        double G(string k, double f = double.NaN) =>
            h.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : f;
        if (!h.TryGetValue("CTYPE1", out var c1) || !c1.Contains("TAN") || !h.TryGetValue("CTYPE2", out var c2) || !c2.Contains("TAN")) return null;
        double v1 = G("CRVAL1"), v2 = G("CRVAL2"), p1 = G("CRPIX1"), p2 = G("CRPIX2");
        if (double.IsNaN(v1) || double.IsNaN(v2) || double.IsNaN(p1) || double.IsNaN(p2)) return null;
        double a = G("CD1_1"), b = G("CD1_2", 0), c = G("CD2_1", 0), d = G("CD2_2");
        if (double.IsNaN(a) || double.IsNaN(d))
        {
            double dx = G("CDELT1"), dy = G("CDELT2");
            if (double.IsNaN(dx) || double.IsNaN(dy)) return null;
            double pa = G("PC1_1", double.NaN);
            if (!double.IsNaN(pa)) { a = dx * pa; b = dx * G("PC1_2", 0); c = dy * G("PC2_1", 0); d = dy * G("PC2_2", 1); }
            else
            {
                double r = G("CROTA2", 0) * D2R;
                a = dx * Math.Cos(r); b = -dy * Math.Sin(r); c = dx * Math.Sin(r); d = dy * Math.Cos(r);
            }
        }
        return new(v1, v2, p1 - 1, p2 - 1, a, b, c, d);
    }

    /// <summary>FITS cards describing this WCS (FITS pixels are 1-based).</summary>
    public IEnumerable<(string Key, string Value)> Cards()
    {
        string F(double v) => v.ToString("G15", CultureInfo.InvariantCulture);
        yield return ("CTYPE1", "'RA---TAN'"); yield return ("CTYPE2", "'DEC--TAN'");
        yield return ("EQUINOX", "2000.0");
        yield return ("CRVAL1", F(CrVal1)); yield return ("CRVAL2", F(CrVal2));
        yield return ("CRPIX1", F(CrPix1 + 1)); yield return ("CRPIX2", F(CrPix2 + 1));
        yield return ("CD1_1", F(Cd11)); yield return ("CD1_2", F(Cd12));
        yield return ("CD2_1", F(Cd21)); yield return ("CD2_2", F(Cd22));
    }

    /// <summary>Rotation from the tangent plane's (xi east, eta north, 1 = towards the tangent point) to equatorial xyz.</summary>
    private double[] Basis()
    {
        double a = CrVal1 * D2R, d = CrVal2 * D2R;
        double sa = Math.Sin(a), ca = Math.Cos(a), sd = Math.Sin(d), cd = Math.Cos(d);
        // columns: east, north, centre
        return [-sa, -sd * ca, cd * ca,
                 ca, -sd * sa, cd * sa,
                 0,   cd,      sd];
    }

    public (double Ra, double Dec) PixelToSky(double x, double y)
    {
        double dx = x - CrPix1, dy = y - CrPix2;
        double xi = (Cd11 * dx + Cd12 * dy) * D2R, eta = (Cd21 * dx + Cd22 * dy) * D2R;
        var m = Basis();
        double X = m[0] * xi + m[1] * eta + m[2], Y = m[3] * xi + m[4] * eta + m[5], Z = m[6] * xi + m[7] * eta + m[8];
        double ra = Math.Atan2(Y, X) / D2R, dec = Math.Atan2(Z, Math.Sqrt(X * X + Y * Y)) / D2R;
        return ((ra % 360 + 360) % 360, dec);
    }

    /// <summary>Pixel of a sky position; NaN for the far hemisphere.</summary>
    public (double X, double Y) SkyToPixel(double raDeg, double decDeg)
    {
        double a = raDeg * D2R, d = decDeg * D2R;
        double X = Math.Cos(d) * Math.Cos(a), Y = Math.Cos(d) * Math.Sin(a), Z = Math.Sin(d);
        var m = Basis();
        double e = m[0] * X + m[3] * Y + m[6] * Z, n = m[1] * X + m[4] * Y + m[7] * Z, c = m[2] * X + m[5] * Y + m[8] * Z;
        if (c <= 1e-9) return (double.NaN, double.NaN);
        return FromStandard(e / c / D2R, n / c / D2R);
    }

    private (double X, double Y) FromStandard(double xiDeg, double etaDeg)
    {
        double det = Cd11 * Cd22 - Cd12 * Cd21;
        return (CrPix1 + (Cd22 * xiDeg - Cd12 * etaDeg) / det, CrPix2 + (-Cd21 * xiDeg + Cd11 * etaDeg) / det);
    }

    /// <summary>The exact map from this grid's pixels to <paramref name="other"/>'s pixels. Two gnomonic projections of the
    /// same sphere are related by a plane homography (both are central projections), so it is a 3x3 matrix H with
    /// (x', y') = (H·(x, y, 1)) dehomogenised. Row-major.</summary>
    public double[] HomographyTo(TanWcs other)
    {
        // pixel -> (xi, eta, 1) in radians
        double[] P = [Cd11 * D2R, Cd12 * D2R, -(Cd11 * CrPix1 + Cd12 * CrPix2) * D2R,
                      Cd21 * D2R, Cd22 * D2R, -(Cd21 * CrPix1 + Cd22 * CrPix2) * D2R,
                      0, 0, 1];
        var A = Basis();
        var B = other.Basis();
        double[] Bt = [B[0], B[3], B[6], B[1], B[4], B[7], B[2], B[5], B[8]];
        // other's (xi, eta, 1) -> pixel, xi/eta in radians
        double det = other.Cd11 * other.Cd22 - other.Cd12 * other.Cd21;
        double i11 = other.Cd22 / det / D2R, i12 = -other.Cd12 / det / D2R, i21 = -other.Cd21 / det / D2R, i22 = other.Cd11 / det / D2R;
        double[] Q = [i11, i12, other.CrPix1, i21, i22, other.CrPix2, 0, 0, 1];
        return Mul(Q, Mul(Bt, Mul(A, P)));
    }

    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }
}
