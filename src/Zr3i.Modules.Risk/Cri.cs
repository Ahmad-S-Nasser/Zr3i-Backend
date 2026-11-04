using Zr3i.Kernel.Indicators;

namespace Zr3i.Modules.Risk;

/// <summary>The season features one parcel's CRI is computed from (methodology FRI-2026-01, section 6). A null feature is never imputed.</summary>
public sealed record CriFeatures(
    double? NdmiMin, double? HeatStress, double? NdviIntegral, double? AreaVariancePct, double? NdviCohortPercentile,
    IReadOnlyList<double>? ClosedSeasonYields, double ValidFraction);

public sealed record CriComputation(
    IReadOnlyList<IndicatorComponent> Components, double? Value, int EstablishedCount, double EstablishedWeight, double ValidFraction, bool Insufficient);

/// <summary>Piecewise-linear ramps and the yield stability formula (methodology section 7). Each returns null when its feature is null.</summary>
public static class ComponentScores
{
    /// <summary>score = s_lo + clamp((v - v_lo) / (v_hi - v_lo), 0, 1) × (s_hi - s_lo)</summary>
    public static double Ramp(double v, double vLo, double vHi, double sLo, double sHi) =>
        sLo + Math.Clamp((v - vLo) / (vHi - vLo), 0, 1) * (sHi - sLo);

    public static double? WaterSecurity(double? ndmiMin) => ndmiMin is { } v && double.IsFinite(v) ? Ramp(v, 15, 45, 85, 25) : null;
    public static double? ClimateExposure(double? heatStress) => heatStress is { } v && double.IsFinite(v) ? Ramp(v, 0, 1, 20, 75) : null;
    public static double? SoilAndLand(double? ndviIntegral) => ndviIntegral is { } v && double.IsFinite(v) ? Ramp(v, 20, 55, 75, 35) : null;
    public static double? ManagementQuality(double? ndviIntegral) => ndviIntegral is { } v && double.IsFinite(v) ? Ramp(v, 25, 55, 55, 25) : null;
    public static double? TenureAndRights(double? areaVariancePct) => areaVariancePct is { } v && double.IsFinite(v) ? Ramp(v, 0, 25, 25, 70) : null;
    public static double? PeerAndContext(double? cohortPercentile) => cohortPercentile is { } v && double.IsFinite(v) ? Ramp(v, 0, 100, 75, 25) : null;

    /// <summary>
    /// score = 15 + f × 45 + d × 25 + min(cv, 1) × 15, clamped to 0 to 100. f is the share of closed seasons below 80 percent of the
    /// parcel's median yield, d is 1 - minimum/median clamped to 0 to 1, cv is the population standard deviation over the mean.
    /// Fewer than two closed seasons, or no usable median, means not established.
    /// </summary>
    public static double? YieldStability(IReadOnlyList<double>? closedSeasonYields)
    {
        if (closedSeasonYields is null || closedSeasonYields.Count < 2 || closedSeasonYields.Any(y => !double.IsFinite(y) || y < 0)) return null;
        var sorted = closedSeasonYields.Order().ToArray();
        var n = sorted.Length;
        var median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        var mean = sorted.Average();
        if (median <= 0 || mean <= 0) return null;

        var f = (double)sorted.Count(y => y < 0.8 * median) / n;
        var d = Math.Clamp(1 - sorted[0] / median, 0, 1);
        var cv = Math.Sqrt(sorted.Sum(y => (y - mean) * (y - mean)) / n) / mean;
        return Math.Clamp(15 + f * 45 + d * 25 + Math.Min(cv, 1) * 15, 0, 100);
    }
}

/// <summary>The CRI: the weighted mean of the component scores, renormalised over the weight that could be established (methodology section 8).</summary>
public static class CriCalculator
{
    public const string MethodId = "FRI-2026-01";
    public const int MethodVersion = 1;

    /// <summary>Below this share of usable observations a parcel-season is insufficient and leaves the farm roll-up (section 6.1).</summary>
    public const double ValidityFloor = 0.60;

    public static CriComputation Compute(CriFeatures f)
    {
        IndicatorComponent C(string key, double weight, double? score) => new(key, weight, score, score is not null);
        IndicatorComponent[] components =
        [
            C("water_security", 25, ComponentScores.WaterSecurity(f.NdmiMin)),
            C("climate_exposure", 20, ComponentScores.ClimateExposure(f.HeatStress)),
            C("soil_and_land", 15, ComponentScores.SoilAndLand(f.NdviIntegral)),
            C("yield_stability", 15, ComponentScores.YieldStability(f.ClosedSeasonYields)),
            C("management_quality", 10, ComponentScores.ManagementQuality(f.NdviIntegral)),
            C("tenure_and_rights", 8, ComponentScores.TenureAndRights(f.AreaVariancePct)),
            C("peer_and_context", 7, ComponentScores.PeerAndContext(f.NdviCohortPercentile)),
        ];

        var established = components.Where(c => c.Established).ToArray();
        var weight = established.Sum(c => c.Weight);
        double? value = established.Length == 0 ? null : established.Sum(c => c.Score!.Value * c.Weight) / weight;
        return new CriComputation(components, value, established.Length, weight, f.ValidFraction, f.ValidFraction < ValidityFloor);
    }
}
