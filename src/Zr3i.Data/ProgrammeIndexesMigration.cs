using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Tranches and event windows: one per organisation, season and name.</summary>
public sealed class ProgrammeIndexesMigration : IMigration
{
    public string Id => "0012-programme-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Tranches, true, ct, "TenantId", "Season", "Name");
        await IndexBuilder.Ascending(database, Collections.EventWindows, true, ct, "TenantId", "Season", "Name");
    }
}
