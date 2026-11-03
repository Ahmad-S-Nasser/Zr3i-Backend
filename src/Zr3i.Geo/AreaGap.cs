namespace Zr3i.Geo;

public enum AreaGapDirection
{
    /// <summary>The gap is not larger than the field's own measurement noise. Never flagged.</summary>
    WithinNoise,

    /// <summary>Declared area exceeds measured area.</summary>
    OverDeclared,

    /// <summary>Measured area exceeds declared area.</summary>
    UnderDeclared,
}

/// <param name="GapFeddan">Signed: measured minus declared. Kept as it is for the audit trail.</param>
/// <param name="GapPercent">Absolute gap as a percentage of the declared area (the FRI-2026-01 "area variance" formula).</param>
/// <param name="NoisePercent">The field's own measurement noise as a percentage of its measured area.</param>
public sealed record AreaGapResult(
    double MeasuredFeddan, double DeclaredFeddan, double GapFeddan, double GapPercent, double NoisePercent, bool Flagged, AreaGapDirection Direction);

/// <summary>
/// Measured against declared area, with the field's own noise (Product Explained 3A.2). The area is geometry on the walked boundary,
/// not pixel counting, so the noise is the boundary error spread along the perimeter: noise % = 100 x perimeter x offset / area.
/// The offset is a planning assumption (2 m), a parameter of the method, never a constant in the engine. A gap is flagged only when it
/// is larger than the multiplier times the noise; a gap inside the noise is never flagged. Two-sided. Pure arithmetic, no I/O.
/// </summary>
public static class AreaGap
{
    public static double NoisePercent(double areaM2, double perimeterM, double boundaryOffsetM)
    {
        Positive(areaM2, nameof(areaM2));
        Positive(perimeterM, nameof(perimeterM));
        if (!double.IsFinite(boundaryOffsetM) || boundaryOffsetM < 0) throw new ArgumentOutOfRangeException(nameof(boundaryOffsetM));
        return 100 * perimeterM * boundaryOffsetM / areaM2;
    }

    public static AreaGapResult Evaluate(double measuredAreaM2, double declaredFeddan, double perimeterM, double boundaryOffsetM, double flagMultiplier)
    {
        Positive(declaredFeddan, nameof(declaredFeddan));
        Positive(flagMultiplier, nameof(flagMultiplier));

        var noise = NoisePercent(measuredAreaM2, perimeterM, boundaryOffsetM);
        var measuredFeddan = measuredAreaM2 / GeoConstants.SquareMetresPerFeddan;
        var gapFeddan = measuredFeddan - declaredFeddan;
        var gapPercent = Math.Abs(gapFeddan) / declaredFeddan * 100;

        var flagged = gapPercent > flagMultiplier * noise;
        var direction = !flagged ? AreaGapDirection.WithinNoise : gapFeddan < 0 ? AreaGapDirection.OverDeclared : AreaGapDirection.UnderDeclared;
        return new AreaGapResult(measuredFeddan, declaredFeddan, gapFeddan, gapPercent, noise, flagged, direction);
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name, value, "Must be a finite number greater than zero.");
    }
}
