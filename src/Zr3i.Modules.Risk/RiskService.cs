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

/// <summary>
/// Records and reads CRI and FRI results for one organisation. Features and adjustments are supplied by the caller; this assembles the
/// farm roll-up from the CRIs already recorded and never ranks or sorts anything by value.
/// </summary>
public sealed class RiskService(KernelStore store, CriService cri, FriService fri, ReferenceService references, AuditService audit)
{
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
        var s = season?.Trim() ?? string.Empty;
        if (!FarmRules.IsValidSeason(s)) throw new ValidationFailedException("invalid_season");
        _ = await store.Farms.GetAsync(scope, farmId, ct) ?? throw new NotFoundException("farm", farmId);

        var seasons = await AllAsync((c, n) => store.ParcelSeasons.FindPageAsync(scope, p => p.FarmId == farmId && p.Season == s, c, n, ct));
        var parcelIds = seasons.Select(p => p.ParcelId).Distinct().ToList();
        var parcels = (await store.Parcels.FindAsync(scope, p => parcelIds.Contains(p.Id), 1000, ct)).ToDictionary(p => p.Id);
        var boundaryIds = parcels.Values.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var boundaries = (await store.Boundaries.FindAsync(scope, b => boundaryIds.Contains(b.Id), 1000, ct)).ToDictionary(b => b.Id);

        var rollup = new List<FriParcel>();
        var skipped = new List<SkippedParcel>();
        foreach (var ps in seasons.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var latest = (await HistoryAsync(scope, IndicatorKinds.Cri, "parcel_season", ps.Id, ct)).LastOrDefault(v => v.Current)?.Result;
            if (latest is null) { skipped.Add(new SkippedParcel(ps.Id, "no_cri")); continue; }
            if (!parcels.TryGetValue(ps.ParcelId, out var parcel) || parcel.CurrentBoundaryId is not { } bid || !boundaries.TryGetValue(bid, out var boundary) || boundary.AreaFeddan <= 0)
            { skipped.Add(new SkippedParcel(ps.Id, "no_boundary")); continue; }
            rollup.Add(new FriParcel(ps.Id, boundary.AreaFeddan, latest.Value, latest.Insufficient));
        }

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
        var history = await HistoryAsync(scope, IndicatorKinds.Cri, "parcel_season", parcelSeasonId, ct);
        var farm = await store.Farms.GetAsync(scope, ps.FarmId, ct);

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
