using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Indicators;

namespace Zr3i.Modules.Risk;

/// <summary>A recorded result with whether it is the current one and, for a current CRI, its band when a matching reference exists.</summary>
public sealed record IndicatorView(IndicatorResult Result, bool Current, BandAssessment? Band, string? BandReason);

public sealed record SkippedParcel(string ParcelSeasonId, string Reason);

public sealed record FriOutcome(IndicatorResult Result, IReadOnlyList<SkippedParcel> Skipped);

/// <summary>What a parcel risk page needs to say what it is about.</summary>
public sealed record ParcelSubject(string ParcelSeasonId, string ParcelId, string Label, string FarmId, string? FarmName, string Season, string? Crop, double? AreaFeddan);

public sealed record ParcelRiskSummary(ParcelSubject Subject, IndicatorView? Current, IReadOnlyList<IndicatorView> History);

public static class ParcelRowStatus
{
    /// <summary>Has a CRI, a boundary, and passed the validity floor: enters the farm mean.</summary>
    public const string Included = "included";

    /// <summary>Failed the validity floor: listed, scored where possible, left out of the mean, never counted as low risk.</summary>
    public const string Insufficient = "insufficient";
    public const string NoCri = "no_cri";
    public const string NoBoundary = "no_boundary";
}

public sealed record FarmParcelRow(
    string ParcelSeasonId, string ParcelId, string Label, string? Crop, double? AreaFeddan, double? Cri, double? AreaTimesCri, string Status, int? ClosedSeasons,
    BandAssessment? Band, string? BandReason);

/// <summary>The parcel whose removal lowers the area-weighted mean the most (methodology 9.3). Only the mean is given: a counterfactual FRI needs the adjustment formulas.</summary>
public sealed record ExposureView(string ParcelSeasonId, string Label, string? Crop, double AreaFeddan, double Cri, double AreaSharePercent, double AwcriWithout);

public sealed record FarmRiskSummary(
    Farm Farm, string Season, IndicatorView? Fri, double? Awcri, double AdjustmentsSum, int ParcelCount, double TotalAreaFeddan, int? SeasonsOnFile, bool FriStale,
    IReadOnlyList<FarmParcelRow> Parcels, ExposureView? Exposure);

/// <summary>
/// Records and reads CRI and FRI results for one organisation. Features and adjustments are supplied by the caller; this assembles the
/// farm roll-up from the CRIs already recorded and never ranks or sorts anything by value.
/// </summary>
public sealed class RiskService(KernelStore store, CriService cri, FriService fri, ReferenceService references, AuditService audit)
{
    private sealed record Assembled(ParcelSeason Season, string Label, double? AreaFeddan, IndicatorResult? Cri, string Status);

    /// <exception cref="NotFoundException">parcel_season.</exception>
    /// <exception cref="ValidationFailedException">Any code from <see cref="CriService"/>.</exception>
    public async Task<IndicatorResult> RecordCriAsync(TenantScope scope, string actorUserId, string parcelSeasonId, CriFeatures features, CancellationToken ct = default)
    {
        _ = await store.ParcelSeasons.GetAsync(scope, parcelSeasonId, ct) ?? throw new NotFoundException("parcel_season", parcelSeasonId);
        var result = await cri.RecordAsync(scope, actorUserId, parcelSeasonId, features, ct);
        await audit.RecordAsync(scope, actorUserId, "risk.cri_recorded", "parcel_season", parcelSeasonId, ct: ct);
        return result;
    }

    /// <exception cref="NotFoundException">farm.</exception>
    /// <exception cref="ValidationFailedException">invalid_season, or any code from <see cref="FriService"/>.</exception>
    public async Task<FriOutcome> RecordFriAsync(TenantScope scope, string actorUserId, string farmId, string season, IReadOnlyList<IndicatorAdjustment> adjustments, CancellationToken ct = default)
    {
        var s = CleanSeason(season);
        _ = await store.Farms.GetAsync(scope, farmId, ct) ?? throw new NotFoundException("farm", farmId);

        var assembled = await AssembleAsync(scope, farmId, s, ct);
        var rollup = assembled.Where(a => a.Status is ParcelRowStatus.Included or ParcelRowStatus.Insufficient)
            .Select(a => new FriParcel(a.Season.Id, a.AreaFeddan!.Value, a.Cri!.Value, a.Status == ParcelRowStatus.Insufficient)).ToList();
        var skipped = assembled.Where(a => a.Status is ParcelRowStatus.NoCri or ParcelRowStatus.NoBoundary).Select(a => new SkippedParcel(a.Season.Id, a.Status)).ToList();

        var result = await fri.RecordAsync(scope, actorUserId, $"{farmId}:{s}", rollup, adjustments, ct);
        await audit.RecordAsync(scope, actorUserId, "risk.fri_recorded", "farm_season", $"{farmId}:{s}", purpose: $"{skipped.Count} skipped", ct: ct);
        return new FriOutcome(result, skipped);
    }

    /// <summary>The history of one subject, oldest first. The newest result nobody has replaced is marked current.</summary>
    public async Task<IReadOnlyList<IndicatorView>> HistoryAsync(TenantScope scope, string indicator, string subjectType, string subjectId, CancellationToken ct = default)
    {
        var rows = (await AllAsync((c, n) => store.IndicatorResults.FindPageAsync(scope, r => r.Indicator == indicator && r.SubjectType == subjectType && r.SubjectId == subjectId, c, n, ct)))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var superseded = rows.Where(r => r.Supersedes != null).Select(r => r.Supersedes!).ToHashSet();
        return [.. rows.Select(r => new IndicatorView(r, !superseded.Contains(r.Id), null, null))];
    }

    /// <summary>A parcel-season's CRI history. The current result is banded against the cohort reference when one exists; the reason is given when it does not.</summary>
    /// <exception cref="NotFoundException">parcel_season.</exception>
    public async Task<IReadOnlyList<IndicatorView>> CriHistoryAsync(TenantScope scope, string parcelSeasonId, CancellationToken ct = default)
    {
        var ps = await store.ParcelSeasons.GetAsync(scope, parcelSeasonId, ct) ?? throw new NotFoundException("parcel_season", parcelSeasonId);
        var farm = await store.Farms.GetAsync(scope, ps.FarmId, ct);
        return await BandHistoryAsync(scope, ps, farm, ct);
    }

    /// <summary>Everything the parcel risk page shows: what the parcel is, the current CRI with features, provenance and band, and the history.</summary>
    /// <exception cref="NotFoundException">parcel_season.</exception>
    public async Task<ParcelRiskSummary> ParcelSummaryAsync(TenantScope scope, string parcelSeasonId, CancellationToken ct = default)
    {
        var ps = await store.ParcelSeasons.GetAsync(scope, parcelSeasonId, ct) ?? throw new NotFoundException("parcel_season", parcelSeasonId);
        var parcel = await store.Parcels.GetAsync(scope, ps.ParcelId, ct);
        var farm = await store.Farms.GetAsync(scope, ps.FarmId, ct);
        var boundary = parcel?.CurrentBoundaryId is { } bid ? await store.Boundaries.GetAsync(scope, bid, ct) : null;
        var history = await BandHistoryAsync(scope, ps, farm, ct);

        var subject = new ParcelSubject(ps.Id, ps.ParcelId, parcel?.Label ?? ps.ParcelId, ps.FarmId, farm?.Name, ps.Season, ps.DeclaredCrop, boundary?.AreaFeddan);
        return new ParcelRiskSummary(subject, history.LastOrDefault(v => v.Current), history);
    }

    /// <summary>
    /// Everything the farm risk page shows: the current FRI and its build-up, the live area-weighted mean, one row per parcel-season (by id, never
    /// by value) with its status, and where the exposure sits. A parcel that cannot be rolled up is listed with the reason.
    /// </summary>
    /// <exception cref="NotFoundException">farm.</exception>
    /// <exception cref="ValidationFailedException">invalid_season.</exception>
    public async Task<FarmRiskSummary> FarmSummaryAsync(TenantScope scope, string farmId, string season, CancellationToken ct = default)
    {
        var s = CleanSeason(season);
        var farm = await store.Farms.GetAsync(scope, farmId, ct) ?? throw new NotFoundException("farm", farmId);
        var assembled = await AssembleAsync(scope, farmId, s, ct);

        var rows = new List<FarmParcelRow>();
        foreach (var a in assembled)
        {
            BandAssessment? band = null;
            string? reason = a.Status;
            if (a.Status == ParcelRowStatus.Included)
                (band, reason) = await BandAsync(scope, a.Season.DeclaredCrop, farm.Governorate, a.Cri!.Value, ct);
            var counted = a.Status == ParcelRowStatus.Included;
            rows.Add(new FarmParcelRow(a.Season.Id, a.Season.ParcelId, a.Label, a.Season.DeclaredCrop, a.AreaFeddan, a.Cri?.Value, counted ? a.AreaFeddan * a.Cri!.Value : null,
                a.Status, a.Cri?.ClosedSeasons, band, reason));
        }

        var included = assembled.Where(a => a.Status == ParcelRowStatus.Included).ToList();
        var includedArea = included.Sum(a => a.AreaFeddan!.Value);
        var weighted = included.Sum(a => a.AreaFeddan!.Value * a.Cri!.Value);
        double? awcri = included.Count == 0 ? null : weighted / includedArea;

        var friView = (await HistoryAsync(scope, IndicatorKinds.Fri, "farm_season", $"{farmId}:{s}", ct)).LastOrDefault(v => v.Current);
        friView = friView is null ? null : friView with { BandReason = "farm_cohort_not_defined" };

        return new FarmRiskSummary(
            farm, s, friView, awcri, friView?.Result.Adjustments.Sum(a => a.Value ?? 0) ?? 0, assembled.Count, assembled.Sum(a => a.AreaFeddan ?? 0),
            assembled.Where(a => a.Cri?.ClosedSeasons is not null).Select(a => a.Cri!.ClosedSeasons).Max(),
            friView is not null && IsStale(friView.Result, included), rows, Exposure(included, awcri, includedArea));
    }

    /// <summary>The recorded FRI is stale when the parcels now entering the mean, with their areas and CRIs, are not the ones it was built from.</summary>
    private static bool IsStale(IndicatorResult fri, List<Assembled> included)
    {
        var recorded = fri.Components.Where(c => c.Established).ToDictionary(c => c.Key, c => (c.Weight, c.Score));
        if (recorded.Count != included.Count) return true;
        return included.Any(a => !recorded.TryGetValue("parcel:" + a.Season.Id, out var r) || r.Weight != a.AreaFeddan || r.Score != a.Cri!.Value);
    }

    private static ExposureView? Exposure(List<Assembled> included, double? awcri, double includedArea)
    {
        if (included.Count < 2 || awcri is not { } mean) return null;
        var sum = included.Sum(a => a.AreaFeddan!.Value * a.Cri!.Value);
        var best = included
            .Select(a => (Parcel: a, Without: (sum - a.AreaFeddan!.Value * a.Cri!.Value) / (includedArea - a.AreaFeddan.Value)))
            .OrderBy(x => x.Without).ThenBy(x => x.Parcel.Label, StringComparer.Ordinal).First();
        var p = best.Parcel;
        return new ExposureView(p.Season.Id, p.Label, p.Season.DeclaredCrop, p.AreaFeddan!.Value, p.Cri!.Value, 100 * p.AreaFeddan.Value / includedArea, best.Without);
    }

    /// <summary>One row per parcel-season of the farm in that season, by label, with its latest CRI, current boundary area and status.</summary>
    private async Task<List<Assembled>> AssembleAsync(TenantScope scope, string farmId, string season, CancellationToken ct)
    {
        var seasons = await AllAsync((c, n) => store.ParcelSeasons.FindPageAsync(scope, p => p.FarmId == farmId && p.Season == season, c, n, ct));
        var parcelIds = seasons.Select(p => p.ParcelId).Distinct().ToList();
        var seasonIds = seasons.Select(p => p.Id).ToList();
        var parcels = (await AllAsync((c, n) => store.Parcels.FindPageAsync(scope, p => parcelIds.Contains(p.Id), c, n, ct))).ToDictionary(p => p.Id);
        var boundaryIds = parcels.Values.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var boundaries = (await AllAsync((c, n) => store.Boundaries.FindPageAsync(scope, b => boundaryIds.Contains(b.Id), c, n, ct))).ToDictionary(b => b.Id);

        var results = await AllAsync((c, n) => store.IndicatorResults.FindPageAsync(scope, r => r.Indicator == IndicatorKinds.Cri && r.SubjectType == "parcel_season" && seasonIds.Contains(r.SubjectId), c, n, ct));
        var superseded = results.Where(r => r.Supersedes != null).Select(r => r.Supersedes!).ToHashSet();
        var latest = results.Where(r => !superseded.Contains(r.Id)).GroupBy(r => r.SubjectId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).Last());

        var assembled = new List<Assembled>();
        foreach (var ps in seasons)
        {
            parcels.TryGetValue(ps.ParcelId, out var parcel);
            var boundary = parcel?.CurrentBoundaryId is { } bid && boundaries.TryGetValue(bid, out var b) && b.AreaFeddan > 0 ? b : null;
            latest.TryGetValue(ps.Id, out var result);
            var status = result is null ? ParcelRowStatus.NoCri : boundary is null ? ParcelRowStatus.NoBoundary : result.Insufficient ? ParcelRowStatus.Insufficient : ParcelRowStatus.Included;
            assembled.Add(new Assembled(ps, parcel?.Label ?? ps.ParcelId, boundary?.AreaFeddan, result, status));
        }

        return [.. assembled.OrderBy(a => a.Label, StringComparer.Ordinal).ThenBy(a => a.Season.Id, StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<IndicatorView>> BandHistoryAsync(TenantScope scope, ParcelSeason ps, Farm? farm, CancellationToken ct)
    {
        var history = await HistoryAsync(scope, IndicatorKinds.Cri, "parcel_season", ps.Id, ct);
        var views = new List<IndicatorView>();
        foreach (var v in history)
        {
            if (!v.Current) { views.Add(v with { BandReason = "not_current" }); continue; }
            var (band, reason) = await BandAsync(scope, ps.DeclaredCrop, farm?.Governorate, v.Result.Value, ct);
            views.Add(v with { Band = band, BandReason = reason });
        }

        return views;
    }

    /// <summary>
    /// Candidates run from the narrowest cohort to the widest: crop and governorate, then the crop nationally. The farm record has no region, so
    /// the region step is skipped.
    /// </summary>
    private async Task<(BandAssessment?, string?)> BandAsync(TenantScope scope, string? crop, string? governorate, double workingScore, CancellationToken ct)
    {
        var c = crop?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c)) return (null, "no_declared_crop");
        var candidates = new List<CohortCandidate>();
        if (!string.IsNullOrWhiteSpace(governorate)) candidates.Add(new CohortCandidate(CohortLevels.CropGovernorate, $"{c}|{governorate.Trim().ToLowerInvariant()}"));
        candidates.Add(new CohortCandidate(CohortLevels.CropNational, c));

        var band = await references.AssessAsync(scope, ReferenceKinds.Parcel, candidates, workingScore, ct);
        return band is null ? (null, "no_reference_distribution") : (band, null);
    }

    /// <summary>A farm-season's FRI history. A farm's cohort is not defined by the methodology, so a farm result is never banded.</summary>
    /// <exception cref="NotFoundException">farm.</exception>
    public async Task<IReadOnlyList<IndicatorView>> FriHistoryAsync(TenantScope scope, string farmId, string season, CancellationToken ct = default)
    {
        _ = await store.Farms.GetAsync(scope, farmId, ct) ?? throw new NotFoundException("farm", farmId);
        var history = await HistoryAsync(scope, IndicatorKinds.Fri, "farm_season", $"{farmId}:{season?.Trim()}", ct);
        return [.. history.Select(v => v with { BandReason = "farm_cohort_not_defined" })];
    }

    public async Task<ReferenceDistribution> AddReferenceAsync(TenantScope scope, string actorUserId, string kind, string level, string cohortKey, IReadOnlyList<string> seasons, IReadOnlyList<double> scores, CancellationToken ct = default)
    {
        var row = await references.AddAsync(scope, actorUserId, kind, level, cohortKey, seasons, scores, ct);
        await audit.RecordAsync(scope, actorUserId, "risk.reference_added", "reference_distribution", row.Id, purpose: $"{kind} {level} {row.CohortKey} v{row.Version}", ct: ct);
        return row;
    }

    private static string CleanSeason(string? season)
    {
        var s = season?.Trim() ?? string.Empty;
        return FarmRules.IsValidSeason(s) ? s : throw new ValidationFailedException("invalid_season");
    }

    private static async Task<List<T>> AllAsync<T>(Func<string?, int, Task<Page<T>>> next)
    {
        var all = new List<T>();
        string? cursor = null;
        do
        {
            var page = await next(cursor, 1000);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }
}
