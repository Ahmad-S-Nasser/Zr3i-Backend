using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Crop configuration: one row per crop and version; activations ordered per crop.</summary>
public sealed class CropConfigIndexesMigration : IMigration
{
    public string Id => "0008-crop-config-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.CropConfigVersions, true, ct, "TenantId", "CropCode", "Version");
        await IndexBuilder.Ascending(database, Collections.CropConfigActivations, true, ct, "TenantId", "CropCode", "Sequence");
    }
}
