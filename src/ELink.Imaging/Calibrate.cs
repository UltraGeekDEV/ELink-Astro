namespace ELink.Imaging;

/// <summary>Calibration of a raw frame (before it is debayered): (light − dark) / flat, the flat normalised to a median of 1.
/// A bias stands in for a dark when no dark suits. Pixels where the flat is nearly black (dust shadows deep enough to be
/// noise, the corners of a vignetted field) are left as they are, divided by nothing.</summary>
public static class Calibrate
{
    public const float MinFlat = 0.05f;

    public static float[] Apply(float[] light, float[]? dark, float[]? flat)
    {
        var r = (float[])light.Clone();
        if (dark is not null)
        {
            if (dark.Length < r.Length) throw new ArgumentException("the dark is smaller than the frame");
            for (int i = 0; i < r.Length; i++) r[i] -= dark[i];
        }
        if (flat is not null)
        {
            if (flat.Length < r.Length) throw new ArgumentException("the flat is smaller than the frame");
            for (int i = 0; i < r.Length; i++) if (flat[i] > MinFlat) r[i] /= flat[i];
        }
        return r;
    }
}
