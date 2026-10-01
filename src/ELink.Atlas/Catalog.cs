namespace ELink.Atlas;

public sealed record CatalogStar(double RaHours, double DecDegrees, float Magnitude, float ColorIndex, int Hd, string Label);

public sealed record CatalogDso(string Id, string Kind, string CommonName, double RaHours, double DecDegrees, float Magnitude,
    float MajorArcmin, float MinorArcmin, float PositionAngle);

public sealed record ConstellationFigure(IReadOnlyList<((double Ra, double Dec) A, (double Ra, double Dec) B)> Segments,
    IReadOnlyList<(string Abbreviation, string Name, double RaHours, double DecDegrees)> Labels);
