using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>One picture of each kind per parcel, boundary version and scene; a parcel's pictures are listed by parcel.</summary>
public sealed class ChipIndexesMigration : IMigration
{
    public string Id => "0015-chip-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.ParcelChips, true, ct, "TenantId", "ParcelId", "BoundaryId", "SceneId", "Kind");
        await IndexBuilder.Ascending(database, Collections.ParcelChips, false, ct, "TenantId", "ParcelId", "SensingTime");
    }
}
