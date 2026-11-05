using Zr3i.Data;
using Zr3i.Kernel.Findings;

namespace Zr3i.Kernel.Farms;

/// <summary>The current area finding for a parcel in a season, as the lists show it.</summary>
public sealed record AreaFindingView(string Status, string? Reason, double? GapPercent, double? NoisePercent, string? Direction);

public sealed record ParcelListItem(
    string Id, string Label, string FarmId, string FarmName, string? Crop, double? DeclaredAreaFeddan, double? MeasuredAreaFeddan, AreaFindingView? Finding);

public sealed record ParcelList(IReadOnlyList<ParcelListItem> Items, string? NextCursor, string? Season);

/// <summary>
/// A page of the organisation's parcels. With a season it adds what was declared that season and the current area finding (the newest one
/// nobody has replaced). The order is stable (creation order); nothing here ranks parcels.
/// </summary>
public sealed class ParcelListService(KernelStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <exception cref="ValidationFailedException">invalid_page_size, invalid_season.</exception>
    public async Task<ParcelList> ListAsync(TenantScope scope, string? cursor, int? pageSize, string? season, string? farmId, CancellationToken ct = default)
    {
        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize) throw new ValidationFailedException("invalid_page_size");
        var s = string.IsNullOrWhiteSpace(season) ? null : season.Trim();
        if (s is not null && !FarmRules.IsValidSeason(s)) throw new ValidationFailedException("invalid_season");
        var farm = string.IsNullOrWhiteSpace(farmId) ? null : farmId.Trim();

        var page = await store.Parcels.FindPageAsync(scope, p => farm == null || p.FarmId == farm, cursor, size, ct);
        var parcelIds = page.Items.Select(p => p.Id).ToList();
        var farmIds = page.Items.Select(p => p.FarmId).Distinct().ToList();
        var farmNames = (await store.Farms.FindAsync(scope, f => farmIds.Contains(f.Id), 1000, ct)).ToDictionary(f => f.Id, f => f.Name);
        var boundaryIds = page.Items.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        var areas = (await store.Boundaries.FindAsync(scope, b => boundaryIds.Contains(b.Id), 1000, ct)).ToDictionary(b => b.Id, b => b.AreaFeddan);

        var seasons = new Dictionary<string, ParcelSeason>();
        var findings = new Dictionary<string, Finding>();
        if (s is not null)
        {
            seasons = (await store.ParcelSeasons.FindAsync(scope, ps => ps.Season == s && parcelIds.Contains(ps.ParcelId), 1000, ct)).GroupBy(ps => ps.ParcelId).ToDictionary(g => g.Key, g => g.First());
            var rows = await store.Findings.FindAsync(scope, f => f.Season == s && f.Slot == FindingSlots.Area && parcelIds.Contains(f.ParcelId), 1000, ct);
            var superseded = rows.Where(f => f.Supersedes != null).Select(f => f.Supersedes!).ToHashSet();
            findings = rows.Where(f => !superseded.Contains(f.Id)).GroupBy(f => f.ParcelId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id, StringComparer.Ordinal).Last());
        }

        var items = page.Items.Select(p =>
        {
            seasons.TryGetValue(p.Id, out var ps);
            findings.TryGetValue(p.Id, out var f);
            double? measured = p.CurrentBoundaryId is { } b && areas.TryGetValue(b, out var a) ? a : null;
            return new ParcelListItem(p.Id, p.Label, p.FarmId, farmNames.GetValueOrDefault(p.FarmId, string.Empty), ps?.DeclaredCrop, ps?.DeclaredAreaFeddan, measured,
                f is null ? null : new AreaFindingView(f.Status, f.Reason, f.GapPercent, f.NoisePercent, f.Direction));
        }).ToList();

        return new ParcelList(items, page.NextCursor, s);
    }
}
