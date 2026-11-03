using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>One row per method version: {tenantId, methodId, version} is unique.</summary>
public sealed class MethodIndexesMigration : IMigration
{
    public string Id => "0006-method-indexes";

    public Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        IndexBuilder.Ascending(database, Collections.MethodVersions, true, ct, "TenantId", "MethodId", "Version");
}
