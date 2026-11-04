using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Reference distributions: one row per kind, cohort and version.</summary>
public sealed class ReferenceIndexesMigration : IMigration
{
    public string Id => "0011-reference-distribution-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        await IndexBuilder.Ascending(database, Collections.ReferenceDistributions, true, ct, "TenantId", "Kind", "CohortLevel", "CohortKey", "Version");
}
