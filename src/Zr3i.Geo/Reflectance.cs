using System.Globalization;

namespace Zr3i.Geo;

/// <param name="BoaOffsetApplied">The provider says the -1000 count offset has already been applied (for example Earth Search's earthsearch:boa_offset_applied).</param>
/// <param name="ProcessingBaseline">The product's processing baseline such as "05.13". Needed only when the offset has not been applied.</param>
public sealed record SceneRadiometry(bool BoaOffsetApplied, string? ProcessingBaseline);

/// <summary>
/// Sentinel-2 L2A digital numbers to surface reflectance. A derived calculation, not a choice: reflectance is DN x 0.0001, minus the
/// 1000-count offset first for native products from processing baseline 04.00 (25 January 2022) onwards, unless the provider has
/// already applied it. The offset is never applied twice. Checked against the W1-5 spike scene: red values down to 246 are only
/// possible if the offset was already applied, as that scene's metadata said.
/// </summary>
public static class Reflectance
{
    public const double Scale = 0.0001;
    public const int BoaAddOffset = -1000;
    private const double OffsetBaseline = 4.0;

    /// <returns>Null for DN 0, which is no data.</returns>
    /// <exception cref="ArgumentException">The offset has not been applied and the baseline is unknown, so the right conversion cannot be chosen.</exception>
    public static double? FromDn(int dn, SceneRadiometry scene)
    {
        if (dn == 0) return null;
        if (scene.BoaOffsetApplied) return dn * Scale;

        if (!double.TryParse(scene.ProcessingBaseline, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseline))
            throw new ArgumentException("The processing baseline is needed to convert a native product.", nameof(scene));
        return baseline >= OffsetBaseline ? (dn + BoaAddOffset) * Scale : dn * Scale;
    }

    /// <summary>A real surface cannot reflect less than nothing or more than about one and a half (bright sand, snow edges); anything else means the conversion is wrong.</summary>
    public static bool IsPlausible(double reflectance) => reflectance is >= 0 and <= 1.5;
}

public static class Indices
{
    /// <summary>(a - b) / (a + b), as NDVI uses with B8 and B4. Null when both are zero.</summary>
    public static double? NormalisedDifference(double a, double b) => a + b == 0 ? null : (a - b) / (a + b);
}

/// <param name="Count">Pixels that contributed.</param>
/// <param name="NoData">Pixels left out because a band was 0 (no data).</param>
/// <param name="Implausible">Pixels left out because the converted reflectance was not physically possible.</param>
public sealed record NdviSummary(int Count, int NoData, int Implausible, double Median, double Mean, double Min, double Max)
{
    /// <summary>NDVI = (NIR - red) / (NIR + red) from reflectance, over the given (B8, B4) digital numbers. Null when no pixel is valid: no value is never zero.</summary>
    public static NdviSummary? Of(IEnumerable<(int Nir, int Red)> pixels, SceneRadiometry scene)
    {
        var values = new List<double>();
        int noData = 0, implausible = 0;
        foreach (var (nirDn, redDn) in pixels)
        {
            if (Reflectance.FromDn(nirDn, scene) is not { } nir || Reflectance.FromDn(redDn, scene) is not { } red) { noData++; continue; }
            if (!Reflectance.IsPlausible(nir) || !Reflectance.IsPlausible(red)) { implausible++; continue; }
            if (Indices.NormalisedDifference(nir, red) is not { } v) { noData++; continue; }
            values.Add(v);
        }

        if (values.Count == 0) return null;
        values.Sort();
        var mid = values.Count / 2;
        var median = values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
        return new NdviSummary(values.Count, noData, implausible, median, values.Average(), values[0], values[^1]);
    }
}
