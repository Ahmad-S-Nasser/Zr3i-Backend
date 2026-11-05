using Zr3i.Data;
using Zr3i.Kernel.Findings;

namespace Zr3i.Kernel.Farms;

/// <summary>The current area finding for a parcel in a season, as the lists show it.</summary>
public sealed record AreaFindingView(string Status, string? Reason, double? GapPercent, double? NoisePercent, string? Direction);

public sealed record ParcelListItem(
    string Id, string Label, string FarmId, string FarmName, string? Crop, double? DeclaredAreaFeddan, double? MeasuredAreaFeddan, AreaFindingView? Finding);

public sealed record ParcelList(IReadOnlyList<ParcelListItem> Items, string? NextCursor, string? Season);

public static class Verdicts
{
    public const string None = "none";
    public static readonly IReadOnlyList<string> All = ["MEASURED", "FLAGGED", "PENDING", "NOT_ASSESSABLE", None];
}

/// <summary>
/// A page of the organisation's parcels. With a season it adds what was declared that season and the current area finding (the newest one
/// nobody has replaced), and the list can be narrowed to one verdict (or none yet) and searched by label. The order is stable (creation
/// order); nothing here ranks parcels.
/// </summary>
public sealed class ParcelListService(KernelStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <exception cref="ValidationFailedException">invalid_page_size, invalid_season, invalid_verdict, season_required.</exception>
    public async Task<ParcelList> ListAsync(TenantScope scope, string? cursor, int? pageSize, string? season, string? farmId, CancellationToken ct = default, string? verdict = null, string? q = null)
    {
        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize) throw new ValidationFailedException("invalid_page_size");
        var s = CleanSeason(season);
        var farm = string.IsNullOrWhiteSpace(farmId) ? null : farmId.Trim();
        var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToLowerInvariant();
        var wanted = string.IsNullOrWhiteSpace(verdict) ? null : verdict.Trim();
        if (wanted is not null && !Verdicts.All.Contains(wanted)) throw new ValidationFailedException("invalid_verdict");
        if (wanted is not null && s is null) throw new ValidationFailedException("season_required");

        HashSet<string>? only = null;
        HashSet<string>? except = null;
        if (wanted is not null)
        {
            var current = await CurrentAreaFindingsAsync(scope, s!, ct);
            if (wanted == Verdicts.None) except = [.. current.Keys];
            else only = [.. current.Where(kv => kv.Value.Status == wanted).Select(kv => kv.Key)];
        }

        var page = await store.Parcels.FindPageAsync(scope,
            p => (farm == null || p.FarmId == farm) && (needle == null || p.Label.ToLower().Contains(needle)) && (only == null || only.Contains(p.Id)) && (except == null || !except.Contains(p.Id)),
            cursor, size, ct);

        return new ParcelList(await BuildAsync(scope, page.Items, s, ct), page.NextCursor, s);
    }

    /// <summary>One parcel as the lists show it (null when it is not in this organisation).</summary>
    public async Task<ParcelListItem?> GetAsync(TenantScope scope, string parcelId, string? season, CancellationToken ct = default)
    {
        var parcel = await store.Parcels.GetAsync(scope, parcelId, ct);
        return parcel is null ? null : (await BuildAsync(scope, [parcel], CleanSeason(season), ct)).Single();
    }

    private static string? CleanSeason(string? season)
    {
        var s = string.IsNullOrWhiteSpace(season) ? null : season.Trim();
        return s is not null && !FarmRules.IsValidSeason(s) ? throw new ValidationFailedException("invalid_season") : s;
    }

    /// <summary>The newest area finding nobody has replaced, per parcel, for one season.</summary>
    private async Task<Dictionary<string, Finding>> CurrentAreaFindingsAsync(TenantScope scope, string season, CancellationToken ct, IReadOnlyCollection<string>? parcelIds = null)
    {
        var rows = new List<Finding>();
        string? cursor = null;
        do
        {
            var page = await store.Findings.FindPageAsync(scope, f => f.Season == season && f.Slot == FindingSlots.Area && (parcelIds == null || parcelIds.Contains(f.ParcelId)), cursor, 1000, ct);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var superseded = rows.Where(f => f.Supersedes != null).Select(f => f.Supersedes!).ToHashSet();
        return rows.Where(f => !superseded.Contains(f.Id)).GroupBy(f => f.ParcelId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id, StringComparer.Ordinal).Last());
    }

    private async Task<List<ParcelListItem>> BuildAsync(TenantScope scope, IReadOnlyList<Parcel> parcels, string? season, CancellationToken ct)
    {
        var parcelIds = parcels.Select(p => p.Id).ToList();
        var farmIds = parcels.Select(p => p.FarmId).Distinct().ToList();
        var farmNames = (await store.Farms.FindAsync(scope, f => farmIds.Contains(f.Id), 1000, ct)).ToDictionary(f => f.Id, f => f.Name);
        var boundaryIds = parcels.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var areas = (await store.Boundaries.FindAsync(scope, b => boundaryIds.Contains(b.Id), 1000, ct)).ToDictionary(b => b.Id, b => b.AreaFeddan);

        var seasons = new Dictionary<string, ParcelSeason>();
        var findings = new Dictionary<string, Finding>();
        if (season is not null)
        {
            seasons = (await store.ParcelSeasons.FindAsync(scope, ps => ps.Season == season && parcelIds.Contains(ps.ParcelId), 1000, ct)).GroupBy(ps => ps.ParcelId).ToDictionary(g => g.Key, g => g.First());
            findings = await CurrentAreaFindingsAsync(scope, season, ct, parcelIds);
        }

        return [.. parcels.Select(p =>
        {
            seasons.TryGetValue(p.Id, out var ps);
            findings.TryGetValue(p.Id, out var f);
            double? measured = p.CurrentBoundaryId is { } b && areas.TryGetValue(b, out var a) ? a : null;
            return new ParcelListItem(p.Id, p.Label, p.FarmId, farmNames.GetValueOrDefault(p.FarmId, string.Empty), ps?.DeclaredCrop, ps?.DeclaredAreaFeddan, measured,
                f is null ? null : new AreaFindingView(f.Status, f.Reason, f.GapPercent, f.NoisePercent, f.Direction));
        })];
    }
}
