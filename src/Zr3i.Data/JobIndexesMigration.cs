using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Indexes for the job queue. The second one deliberately does not start with TenantId: the worker's claim looks across
/// tenants for due work, and that query is the one place a tenant-less index is right.
/// </summary>
public sealed class JobIndexesMigration : IMigration
{
    public string Id => "0003-job-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Jobs, false, ct, "TenantId", "Status", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.Jobs, false, ct, "Status", "AvailableAt", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.Jobs, false, ct, "Status", "LockedUntil");
    }
}
