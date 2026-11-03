using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>The scene catalogue: one row per provider scene id, and a way to list by period and tile.</summary>
public sealed class SceneIndexesMigration : IMigration
{
    public string Id => "0007-scene-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Scenes, true, ct, "TenantId", "SceneId");
        await IndexBuilder.Ascending(database, Collections.Scenes, false, ct, "TenantId", "Tile", "SensingTime");
        await IndexBuilder.Ascending(database, Collections.Scenes, false, ct, "TenantId", "SensingTime");
    }
}
