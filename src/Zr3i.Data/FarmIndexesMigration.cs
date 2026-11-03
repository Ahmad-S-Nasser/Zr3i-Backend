using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Indexes for portfolios, farms, parcels, seasons, boundaries and custom fields: {tenantId, farmId}, {tenantId, parcelId, season},
/// a 2dsphere on boundaries, and an external-id key per collection so a re-import cannot create duplicates.
/// </summary>
public sealed class FarmIndexesMigration : IMigration
{
    public string Id => "0002-farm-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Portfolios, false, ct, "TenantId", "Name");
        await IndexBuilder.UniqueExternalId(database, Collections.Portfolios, ct);

        await IndexBuilder.Ascending(database, Collections.Farms, false, ct, "TenantId", "PortfolioId");
        await IndexBuilder.UniqueExternalId(database, Collections.Farms, ct);

        await IndexBuilder.Ascending(database, Collections.Parcels, false, ct, "TenantId", "FarmId");
        await IndexBuilder.UniqueExternalId(database, Collections.Parcels, ct);

        await IndexBuilder.Ascending(database, Collections.ParcelSeasons, true, ct, "TenantId", "ParcelId", "Season");
        await IndexBuilder.Ascending(database, Collections.ParcelSeasons, false, ct, "TenantId", "FarmId", "Season");
        await IndexBuilder.UniqueExternalId(database, Collections.ParcelSeasons, ct);

        await IndexBuilder.Ascending(database, Collections.Boundaries, false, ct, "TenantId", "ParcelId", "CapturedAt");
        await IndexBuilder.Geo(database, Collections.Boundaries, "Geometry", ct);

        await IndexBuilder.Ascending(database, Collections.CustomFieldDefinitions, true, ct, "TenantId", "Entity", "Key");
    }
}
