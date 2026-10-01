using ELink.Core.Astro;

namespace ELink.Atlas;

/// <summary>A simple equal-angle bucket grid on the sphere for cone searches: fast enough for a few hundred thousand objects.</summary>
public sealed class SkyIndex<T>
{
    private const double Cell = 2.0;                 // degrees
    private readonly List<T>[][] _cells;
    private readonly Func<T, (double RaHours, double DecDegrees)> _pos;
    private const int Bands = (int)(180 / Cell);
    private static readonly int RaCells = (int)(360 / Cell);

    public SkyIndex(IEnumerable<T> items, Func<T, (double, double)> position)
    {
        _pos = position;
        _cells = new List<T>[Bands][];
        for (int b = 0; b < Bands; b++) { _cells[b] = new List<T>[RaCells]; for (int c = 0; c < RaCells; c++) _cells[b][c] = new List<T>(); }
        foreach (var item in items)
        {
            var (ra, dec) = position(item);
            _cells[Band(dec)][RaCell(ra)].Add(item);
            Count++;
        }
    }

    public int Count { get; }

    private static int Band(double dec) => Math.Clamp((int)((dec + 90) / Cell), 0, Bands - 1);
    private static int RaCell(double raHours) => (int)(Precession.NormalizeHours(raHours) * 15 / Cell) % RaCells;

    /// <summary>Everything within <paramref name="radius"/> degrees of the centre.</summary>
    public IEnumerable<T> Cone(double raHours, double decDegrees, double radius)
    {
        int b0 = Band(Math.Max(-90, decDegrees - radius)), b1 = Band(Math.Min(90, decDegrees + radius));
        for (int b = b0; b <= b1; b++)
        {
            double bandLow = -90 + b * Cell, bandHigh = bandLow + Cell;
            // the RA half-width the cone needs in this band, at the band's edge nearest the pole (the whole circle near the poles)
            double cosDec = Math.Cos(Math.Max(Math.Abs(bandLow), Math.Abs(bandHigh)) * Math.PI / 180);
            bool allRa = radius >= 90 || Math.Abs(decDegrees) + radius >= 89 || cosDec < 1e-6;
            double halfRa = allRa ? 180 : Math.Min(180, Math.Asin(Math.Min(1, Math.Sin(radius * Math.PI / 180) / cosDec)) * 180 / Math.PI + Cell);
            int span = (int)Math.Ceiling(halfRa / Cell);
            int center = RaCell(raHours);
            var seen = new HashSet<int>();
            for (int k = -span; k <= span; k++)
            {
                int c = ((center + k) % RaCells + RaCells) % RaCells;
                if (!seen.Add(c)) continue;
                foreach (var item in _cells[b][c])
                {
                    var (ra, dec) = _pos(item);
                    if (Sky.SeparationDegrees(raHours, decDegrees, ra, dec) <= radius) yield return item;
                }
            }
        }
    }
}
