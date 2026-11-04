using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;

namespace Zr3i.Modules.Risk;

/// <summary>One parcel-season entering the farm roll-up: its area, its CRI, and whether it failed the validity gate.</summary>
public sealed record FriParcel(string ParcelSeasonId, double AreaFeddan, double Cri, bool Insufficient);

public sealed record FriComputation(double? Awcri, double? Fri, double TotalAreaFeddan, double IncludedAreaFeddan, int Included, int Excluded);

/// <summary>The five farm-level adjustments of methodology section 9.1. Their sizes are not defined by the specification, so they are supplied, never computed here.</summary>
public static class FriAdjustments
{
    public const string CropConcentration = "crop_concentration";
    public const string SpatialConcentration = "spatial_concentration";
    public const string IrrigationDiversity = "irrigation_diversity";
    public const string RecordLength = "record_length";
    public const string WorstParcelExposure = "worst_parcel_exposure";

    public static readonly IReadOnlyList<string> All = [CropConcentration, SpatialConcentration, IrrigationDiversity, RecordLength, WorstParcelExposure];
}

public static class FriCalculator
{
    public const string MethodId = CriCalculator.MethodId;
    public const int MethodVersion = CriCalculator.MethodVersion;

    /// <summary>AWCRI = sum(area × CRI) / sum(area) over parcels that pass the validity gate; FRI = AWCRI + the supplied adjustments (section 9).</summary>
    public static FriComputation Compute(IReadOnlyList<FriParcel> parcels, IReadOnlyList<IndicatorAdjustment> adjustments)
    {
        var included = parcels.Where(p => !p.Insufficient).ToArray();
        var includedArea = included.Sum(p => p.AreaFeddan);
        var total = parcels.Sum(p => p.AreaFeddan);
        if (included.Length == 0) return new FriComputation(null, null, total, 0, 0, parcels.Count);

        var awcri = included.Sum(p => p.AreaFeddan * p.Cri) / includedArea;
        var fri = awcri + adjustments.Sum(a => a.Value ?? 0);
        return new FriComputation(awcri, fri, total, includedArea, included.Length, parcels.Count - included.Length);
    }
}

/// <summary>Computes a farm-season's FRI and records it as an immutable indicator result (maturity "specified").</summary>
public sealed class FriService(IndicatorRecorder recorder)
{
    /// <exception cref="ValidationFailedException">invalid_parcel, duplicate_parcel, unknown_adjustment, no_eligible_parcels, or any recorder code.</exception>
    public Task<IndicatorResult> RecordAsync(TenantScope scope, string actorUserId, string farmSeasonId, IReadOnlyList<FriParcel> parcels, IReadOnlyList<IndicatorAdjustment> adjustments, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (parcels.Any(p => string.IsNullOrWhiteSpace(p.ParcelSeasonId) || !double.IsFinite(p.AreaFeddan) || p.AreaFeddan <= 0 || !double.IsFinite(p.Cri))) errors.Add("invalid_parcel");
        if (parcels.GroupBy(p => p.ParcelSeasonId, StringComparer.Ordinal).Any(g => g.Count() > 1)) errors.Add("duplicate_parcel");
        if (adjustments.Any(a => !FriAdjustments.All.Contains(a.Key))) errors.Add("unknown_adjustment");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var fri = FriCalculator.Compute(parcels, adjustments);
        if (fri.Fri is not { } value) throw new ValidationFailedException("no_eligible_parcels");

        // Each parcel is a component weighted by its area. An excluded parcel keeps its weight, has no score, and is listed as missing.
        var components = parcels.Select(p => p.Insufficient
            ? new IndicatorComponent("parcel:" + p.ParcelSeasonId, p.AreaFeddan, null, false)
            : new IndicatorComponent("parcel:" + p.ParcelSeasonId, p.AreaFeddan, p.Cri, true)).ToList();
        var all = FriAdjustments.All.Select(k => adjustments.FirstOrDefault(a => a.Key == k) ?? new IndicatorAdjustment(k, null)).ToList();

        return recorder.RecordAsync(scope, actorUserId, new IndicatorDraft(
            IndicatorKinds.Fri, "farm_season", farmSeasonId, FriCalculator.MethodId, FriCalculator.MethodVersion, components, value,
            null, false, all), ct);
    }
}
