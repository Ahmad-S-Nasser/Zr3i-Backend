using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Indexes for account types, access requests, farmers and consent.</summary>
public sealed class AccountIndexesMigration : IMigration
{
    public string Id => "0005-account-and-consent-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.AccountTypes, true, ct, "TenantId", "Key");
        await IndexBuilder.Ascending(database, Collections.AccessRequests, false, ct, "TenantId", "Status", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.Farmers, false, ct, "TenantId", "Name");
        await IndexBuilder.UniqueExternalId(database, Collections.Farmers, ct);
        await IndexBuilder.Ascending(database, Collections.Farms, false, ct, "TenantId", "FarmerId");
        await IndexBuilder.Ascending(database, Collections.ConsentRequests, false, ct, "TenantId", "FarmerId");
        await IndexBuilder.Ascending(database, Collections.ConsentEvents, false, ct, "TenantId", "GrantId", "At");
        await IndexBuilder.Ascending(database, Collections.ConsentEvents, false, ct, "TenantId", "FarmerId");
    }
}
