using Zr3i.Data;
using Zr3i.Kernel.Indicators;

namespace Zr3i.Kernel.Farms;

public sealed record FarmListItem(string Id, string Name, string? Governorate, string? Markaz, int ParcelCount, double AreaFeddan, IndicatorResult? Fri);

public sealed record FarmList(IReadOnlyList<FarmListItem> Items, string? NextCursor, string? Season);

/// <summary>
/// A page of the organisation's farms with their parcel count, measured area (current boundaries only) and, when a season is given, the farm's
/// current FRI. The order is stable (creation order) and never depends on risk: there is no ranking.
/// </summary>
public sealed class FarmListService(KernelStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <exception cref="ValidationFailedException">invalid_page_size, invalid_season.</exception>
    public async Task<FarmList> ListAsync(TenantScope scope, string? cursor, int? pageSize, string? season, CancellationToken ct = default)
    {
        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize) throw new ValidationFailedException("invalid_page_size");
        var s = string.IsNullOrWhiteSpace(season) ? null : season.Trim();
        if (s is not null && !FarmRules.IsValidSeason(s)) throw new ValidationFailedException("invalid_season");

        var page = await store.Farms.FindPageAsync(scope, _ => true, cursor, size, ct);
        var farmIds = page.Items.Select(f => f.Id).ToList();

        var parcels = await AllAsync((c, n) => store.Parcels.FindPageAsync(scope, p => farmIds.Contains(p.FarmId), c, n, ct));
        var boundaryIds = parcels.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var areas = (await AllAsync((c, n) => store.Boundaries.FindPageAsync(scope, b => boundaryIds.Contains(b.Id), c, n, ct))).ToDictionary(b => b.Id, b => b.AreaFeddan);
        var byFarm = parcels.GroupBy(p => p.FarmId).ToDictionary(g => g.Key, g => g.ToList());

        var current = new Dictionary<string, IndicatorResult>();
        if (s is not null)
        {
            var subjects = farmIds.Select(id => $"{id}:{s}").ToList();
            var results = await AllAsync((c, n) => store.IndicatorResults.FindPageAsync(scope, r => r.Indicator == IndicatorKinds.Fri && r.SubjectType == "farm_season" && subjects.Contains(r.SubjectId), c, n, ct));
            var superseded = results.Where(r => r.Supersedes != null).Select(r => r.Supersedes!).ToHashSet();
            current = results.Where(r => !superseded.Contains(r.Id)).GroupBy(r => r.SubjectId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).Last());
        }

        var items = page.Items.Select(f =>
        {
            var own = byFarm.GetValueOrDefault(f.Id) ?? [];
            var area = own.Where(p => p.CurrentBoundaryId != null && areas.ContainsKey(p.CurrentBoundaryId)).Sum(p => areas[p.CurrentBoundaryId!]);
            current.TryGetValue($"{f.Id}:{s}", out var fri);
            return new FarmListItem(f.Id, f.Name, f.Governorate, f.Markaz, own.Count, area, fri);
        }).ToList();

        return new FarmList(items, page.NextCursor, s);
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
