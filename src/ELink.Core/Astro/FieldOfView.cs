namespace ELink.Core.Astro;

public static class FieldOfView
{
    /// <summary>Field of view in degrees of a sensor dimension behind an optical system.</summary>
    /// <param name="pixels">sensor size along that axis in pixels</param>
    /// <param name="pixelSizeMicrons">pixel pitch</param>
    /// <param name="focalLengthMm">focal length of the optical system including any reducer or barlow</param>
    public static double Degrees(double pixels, double pixelSizeMicrons, double focalLengthMm)
    {
        if (!(pixels > 0) || !(pixelSizeMicrons > 0) || !(focalLengthMm > 0)) return double.NaN;
        double sizeMm = pixels * pixelSizeMicrons / 1000.0;
        return 2 * Math.Atan(sizeMm / (2 * focalLengthMm)) * 180.0 / Math.PI;
    }

    /// <summary>Plate scale in arcseconds per pixel.</summary>
    public static double ArcsecPerPixel(double pixelSizeMicrons, double focalLengthMm) => 206.265 * pixelSizeMicrons / focalLengthMm;
}
