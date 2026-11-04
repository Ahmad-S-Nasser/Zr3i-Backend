using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Findings: one row per parcel-season, question and input hash, and ways to list by parcel and by season.</summary>
public sealed class FindingIndexesMigration : IMigration
{
    public string Id => "0009-finding-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Findings, true, ct, "TenantId", "ParcelSeasonId", "Slot", "InputSha256", "Supersedes");
        await IndexBuilder.Ascending(database, Collections.Findings, false, ct, "TenantId", "ParcelId", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.Findings, false, ct, "TenantId", "Season", "Type");
    }
}
