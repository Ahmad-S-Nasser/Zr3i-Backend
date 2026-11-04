using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Indicator results: one row per subject, input hash and predecessor, and a way to list a subject's history.</summary>
public sealed class IndicatorIndexesMigration : IMigration
{
    public string Id => "0010-indicator-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.IndicatorResults, true, ct, "TenantId", "Indicator", "SubjectType", "SubjectId", "InputSha256", "Supersedes");
        await IndexBuilder.Ascending(database, Collections.IndicatorResults, false, ct, "TenantId", "SubjectType", "SubjectId", "CreatedAt");
    }
}
