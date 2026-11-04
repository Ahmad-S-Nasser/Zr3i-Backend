using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Crops;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Methods;

namespace Zr3i.Modules.Verification;

/// <summary>
/// Assesses every declared parcel-season of an organisation for one season and records the findings (<see cref="Finding"/>): the area
/// gap between what was declared and what was measured, and the crop evidence. States facts and statuses only. Safe to run again: a
/// finding whose inputs are unchanged is not written twice; changed inputs add a new finding that supersedes the old one.
/// </summary>
public sealed class AssessHandler(KernelStore store, MethodRegistry methods, CropConfigs crops, SizeClassScheme? sizes, TimeProvider clock) : IJobHandler
{
    private const int Page = 200;
    private const int Many = 1000;
    private static readonly string[] ParameterKeys =
    [
        "area_gap.boundary_offset_m", "area_gap.flag_multiplier", "crop.min_valid_scenes", "pass.min_independent",
        "crop.no_crop.bare_after_sowing_days", "pixel.min_full_pixels",
    ];

    public string Type => AssessmentPlanner.JobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var season = context.Payload<AssessPayload>().Season;
        var method = await methods.GetAsync(R0Draft.MethodId, null, ct);
        if (method is null) return JobOutcome.Failure("method_not_found");
        var missing = ParameterKeys.FirstOrDefault(k => !double.TryParse(method.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (missing is not null) return JobOutcome.Failure("method_parameter_missing:" + missing);

        var counts = new Counts();
        var ruleCache = new Dictionary<string, ResolvedCropRule?>();
        string? cursor = null;
        do
        {
            var page = await store.ParcelSeasons.FindPageAsync(scope, s => s.Season == season, cursor, Page, ct);
            await ProcessPageAsync(scope, method, page.Items, ruleCache, counts, ct);
            await context.ReportProgressAsync(counts.Seasons, counts.Seasons, ct);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return JobOutcome.Success(JobJson.Serialize(new { season, parcelSeasons = counts.Seasons, written = counts.Written, unchanged = counts.Unchanged, byStatus = counts.ByStatus }));
    }

    private async Task ProcessPageAsync(
        TenantScope scope, MethodVersion method, IReadOnlyList<ParcelSeason> seasons, Dictionary<string, ResolvedCropRule?> ruleCache, Counts counts, CancellationToken ct)
    {
        counts.Seasons += seasons.Count;
        var parcelIds = seasons.Select(s => s.ParcelId).Distinct().ToList();
        var seasonIds = seasons.Select(s => s.Id).ToList();
        var parcels = (await store.Parcels.FindAsync(scope, p => parcelIds.Contains(p.Id), Page, ct)).ToDictionary(p => p.Id);
        var boundaryIds = parcels.Values.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var boundaries = (await store.Boundaries.FindAsync(scope, b => boundaryIds.Contains(b.Id), Page, ct)).ToDictionary(b => b.Id);
        var observations = (await AllAsync((c, n) => store.ParcelObservations.FindPageAsync(scope, o => parcelIds.Contains(o.ParcelId), c, n, ct))).GroupBy(o => o.ParcelId).ToDictionary(g => g.Key, g => g.ToList());
        var latest = (await AllAsync((c, n) => store.Findings.FindPageAsync(scope, f => seasonIds.Contains(f.ParcelSeasonId), c, n, ct)))
            .GroupBy(f => (f.ParcelSeasonId, f.Slot)).ToDictionary(g => g.Key, g => g.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id, StringComparer.Ordinal).Last());

        var parameters = ParametersOf(method);
        var fresh = new List<Finding>();
        foreach (var season in seasons)
        {
            BoundaryVersion? boundary = null;
            if (parcels.TryGetValue(season.ParcelId, out var parcel) && parcel.CurrentBoundaryId is { } bid) boundaries.TryGetValue(bid, out boundary);

            var crop = season.DeclaredCrop?.Trim().ToLowerInvariant();
            ResolvedCropRule? rule = null;
            if (!string.IsNullOrEmpty(crop))
            {
                if (!ruleCache.TryGetValue(crop, out rule)) ruleCache[crop] = rule = await crops.ResolveAsync(crop, method, ct);
            }

            var used = boundary is null || !observations.TryGetValue(season.ParcelId, out var all) ? [] : ReadableObservations(all, boundary.Id);
            foreach (var candidate in new[] { AreaFinding(season, boundary, method, parameters), CropFinding(season, boundary, rule, method, parameters, used) })
            {
                latest.TryGetValue((season.Id, candidate.Slot), out var previous);
                if (previous is not null && previous.InputSha256 == candidate.InputSha256) { counts.Unchanged++; continue; }
                candidate.Supersedes = previous?.Id;
                candidate.CreatedAt = clock.GetUtcNow().UtcDateTime;
                candidate.CreatedBy = "system:assess";
                fresh.Add(candidate);
            }
        }

        await InsertAsync(scope, fresh, counts, ct);
    }

    /// <summary>Reads every page of a query; the repositories cap one page at 1000 rows.</summary>
    private static async Task<List<T>> AllAsync<T>(Func<string?, int, Task<Page<T>>> next)
    {
        var all = new List<T>();
        string? cursor = null;
        do
        {
            var page = await next(cursor, Many);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }

    private Finding AreaFinding(ParcelSeason season, BoundaryVersion? boundary, MethodVersion method, string parameters)
    {
        var f = Base(season, FindingSlots.Area, boundary, method, parameters);
        f.Type = FindingTypes.AreaGap;
        if (boundary is null) return Seal(f, FindingStatuses.NotAssessable, FindingReasons.NoBoundary, boundary, null);
        if (season.DeclaredAreaFeddan is not > 0) return Seal(f, FindingStatuses.NotAssessable, FindingReasons.MissingDeclaration, boundary, null);
        if (boundary.PerimeterM <= 0 || boundary.AreaM2 <= 0) return Seal(f, FindingStatuses.NotAssessable, FindingReasons.BoundaryIncomplete, boundary, null);

        var gap = AreaGap.Evaluate(boundary.AreaM2, season.DeclaredAreaFeddan.Value, boundary.PerimeterM, method.GetDouble("area_gap.boundary_offset_m"), method.GetDouble("area_gap.flag_multiplier"));
        f.MeasuredFeddan = gap.MeasuredFeddan;
        f.GapFeddan = gap.GapFeddan;
        f.GapPercent = gap.GapPercent;
        f.NoisePercent = gap.NoisePercent;
        f.Direction = gap.Direction.ToString();
        return Seal(f, gap.Flagged ? FindingStatuses.Flagged : FindingStatuses.Measured, null, boundary, null);
    }

    private Finding CropFinding(ParcelSeason season, BoundaryVersion? boundary, ResolvedCropRule? rule, MethodVersion method, string parameters, IReadOnlyList<(ParcelObservation Obs, SceneNdvi Scene)> used)
    {
        var f = Base(season, FindingSlots.Crop, boundary, method, parameters);
        if (boundary is null)
        {
            f.Type = FindingTypes.NotAssessable;
            return Seal(f, FindingStatuses.NotAssessable, FindingReasons.NoBoundary, boundary, null);
        }

        DateOnly? sowing = DateOnly.TryParseExact(season.DeclaredSowingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var below = ReadabilityService.FullPixelsOf(boundary) < (int)method.GetDouble("pixel.min_full_pixels");
        var result = CropEvidence.Evaluate(new CropAssessmentInput(
            below, season.DeclaredCrop, sowing, rule?.Settings, (int)method.GetDouble("crop.min_valid_scenes"), (int)method.GetDouble("pass.min_independent"),
            (int)method.GetDouble("crop.no_crop.bare_after_sowing_days"), [.. used.Select(u => u.Scene)]));

        f.Type = result.Type;
        f.ScenesUsed = result.ScenesUsed;
        f.PassesUsed = result.PassesUsed;
        f.MaxNdvi = result.MaxNdvi;
        f.RulesTested = [.. result.RulesTested];
        f.ObservationIds = [.. used.Select(u => u.Obs.Id).Order(StringComparer.Ordinal)];
        if (rule is not null)
        {
            f.CropRuleSource = rule.Source;
            f.CropConfigId = rule.ConfigId;
            f.CropConfigVersion = rule.ConfigVersion;
            f.CropRuleSha256 = rule.Sha256 ?? Sha(Describe(rule.Settings));
        }

        var evidence = string.Join(',', used.Select(u => u.Obs.Id + ":" + u.Obs.PixelStackSha256).Order(StringComparer.Ordinal));
        return Seal(f, result.Status, result.Reason, boundary, evidence + "|" + (f.CropRuleSha256 ?? "none") + "|" + f.CropRuleSource);
    }

    private Finding Base(ParcelSeason season, string slot, BoundaryVersion? boundary, MethodVersion method, string parameters) => new()
    {
        ParcelId = season.ParcelId, ParcelSeasonId = season.Id, Season = season.Season, Slot = slot,
        DeclaredCrop = season.DeclaredCrop, DeclaredAreaFeddan = season.DeclaredAreaFeddan, DeclaredSowingDate = season.DeclaredSowingDate, DeclaredSource = season.DeclaredSource,
        BoundaryId = boundary?.Id ?? string.Empty, BoundaryGeometrySha256 = boundary?.GeometrySha256 ?? string.Empty,
        MethodId = method.MethodId, MethodVersion = method.Version, MethodState = method.State, ParametersSha256 = Sha(parameters),
    };

    /// <summary>Sets the status, the accuracy reference and the hash of everything that decides this finding.</summary>
    private Finding Seal(Finding f, string status, string? reason, BoundaryVersion? boundary, string? evidence)
    {
        f.Status = status;
        f.Reason = reason;
        var sizeClass = boundary is not null && sizes is not null ? sizes.Classes[sizes.IndexOf(boundary.AreaFeddan)].Key : "unclassified";
        f.AccuracyRef = $"{f.MethodId}/v{f.MethodVersion}/{f.Type}/{(f.Slot == FindingSlots.Crop ? f.DeclaredCrop ?? "-" : "-")}/{sizeClass}";
        f.InputSha256 = Sha(string.Join('|', f.Slot, f.Type, f.Status, f.Reason, f.DeclaredCrop, f.DeclaredAreaFeddan?.ToString("R", CultureInfo.InvariantCulture), f.DeclaredSowingDate,
            f.BoundaryId, f.BoundaryGeometrySha256, f.MethodId, f.MethodVersion, f.MethodState, f.ParametersSha256, f.AccuracyRef, evidence));
        return f;
    }

    private async Task InsertAsync(TenantScope scope, List<Finding> fresh, Counts counts, CancellationToken ct)
    {
        if (fresh.Count == 0) return;
        try
        {
            await store.Findings.InsertManyAsync(scope, fresh, ct);
        }
        catch (DuplicateKeyException)
        {
            // Another worker wrote some of these at the same moment. Write one by one and skip what is already there.
            foreach (var f in fresh)
            {
                try { await store.Findings.InsertAsync(scope, f, ct); }
                catch (DuplicateKeyException) { counts.Unchanged++; counts.Written--; }
            }
        }

        counts.Written += fresh.Count;
        foreach (var f in fresh) counts.ByStatus[f.Status] = counts.ByStatus.GetValueOrDefault(f.Status) + 1;
    }

    /// <summary>Readable observations of the current boundary with an NDVI, one per acquisition date (two sensors on one day are one pass).</summary>
    private static List<(ParcelObservation Obs, SceneNdvi Scene)> ReadableObservations(IEnumerable<ParcelObservation> all, string boundaryId) =>
        [.. all.Where(o => o.BoundaryId == boundaryId && o.Readable && o.NdviMedian is not null)
            .GroupBy(o => DateOnly.FromDateTime(o.SensingTime))
            .Select(g => g.OrderBy(o => o.SceneId, StringComparer.Ordinal).First())
            .OrderBy(o => o.SensingTime)
            .Select(o => (o, new SceneNdvi(DateOnly.FromDateTime(o.SensingTime), o.NdviMedian!.Value)))];

    private static string ParametersOf(MethodVersion m) => string.Join('\n', ParameterKeys.Select(k => k + "=" + m.Get(k)));

    private static string Describe(CropSettings s) =>
        string.Join('|', s.EmergenceToleranceDays, s.PassMinSeparationDays, s.SowingWindow, s.CycleDays,
            string.Join(',', s.NdviThresholds.Select(t => FormattableString.Invariant($"{t.Key}={t.Value:R}"))));

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Counts
    {
        public int Seasons, Written, Unchanged;
        public Dictionary<string, int> ByStatus { get; } = [];
    }
}
