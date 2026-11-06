using Zr3i.Data;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel.Search;

public sealed record SearchHit(string Id, string Label, string? Context);

/// <summary>One group of hits. A group the role cannot read is not returned at all (absent, not empty).</summary>
public sealed record SearchGroup(string Kind, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// The top-bar search over the organisation's own data. At least two characters, at most five hits a group, ordered by name (never by
/// risk value: there is no ranking). It reads through the tenant-scoped repositories, so another organisation's rows cannot appear.
/// A farmer's phone number is neither matched nor returned.
/// </summary>
public sealed class SearchService(KernelStore store)
{
    public const int MinLength = 2;
    public const int PerGroup = 5;

    public static class Kinds
    {
        public const string Farms = "farms";
        public const string Parcels = "parcels";
        public const string Farmers = "farmers";
        public const string Reports = "reports";
    }

    /// <exception cref="ValidationFailedException">query_too_short.</exception>
    public async Task<IReadOnlyList<SearchGroup>> SearchAsync(TenantScope scope, Access access, string? query, CancellationToken ct = default)
    {
        var needle = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (needle.Length < MinLength) throw new ValidationFailedException("query_too_short");
        var groups = new List<SearchGroup>();

        if (access.Allows("portfolio:farms:read"))
        {
            var farms = await store.Farms.FindOrderedAsync(scope, f => f.Name.ToLower().Contains(needle) || (f.ExternalId != null && f.ExternalId.ToLower().Contains(needle)), f => f.Name, limit: PerGroup, ct: ct);
            groups.Add(new SearchGroup(Kinds.Farms, [.. farms.Select(f => new SearchHit(f.Id, f.Name, f.Governorate))]));

            var parcels = await store.Parcels.FindOrderedAsync(scope, p => p.Label.ToLower().Contains(needle) || (p.ExternalId != null && p.ExternalId.ToLower().Contains(needle)), p => p.Label, limit: PerGroup, ct: ct);
            var farmIds = parcels.Select(p => p.FarmId).Distinct().ToList();
            var farmNames = (await store.Farms.FindAsync(scope, f => farmIds.Contains(f.Id), PerGroup * 2, ct)).ToDictionary(f => f.Id, f => f.Name);
            groups.Add(new SearchGroup(Kinds.Parcels, [.. parcels.Select(p => new SearchHit(p.Id, p.Label, farmNames.GetValueOrDefault(p.FarmId)))]));
        }

        if (access.Allows("kernel:farmers:read"))
        {
            var farmers = await store.Farmers.FindOrderedAsync(scope, f => f.Name.ToLower().Contains(needle) || (f.ExternalId != null && f.ExternalId.ToLower().Contains(needle)), f => f.Name, limit: PerGroup, ct: ct);
            groups.Add(new SearchGroup(Kinds.Farmers, [.. farmers.Select(f => new SearchHit(f.Id, f.Name, f.ExternalId))]));
        }

        if (access.Allows("kernel:reports:read"))
        {
            var reports = await store.FrozenReports.FindOrderedAsync(scope, r => r.SubjectName.ToLower().Contains(needle) || r.Kind.Contains(needle), r => r.SubjectName, limit: PerGroup, ct: ct);
            groups.Add(new SearchGroup(Kinds.Reports, [.. reports.Select(r => new SearchHit(r.Id, r.SubjectName, r.Kind))]));
        }

        return groups;
    }
}
