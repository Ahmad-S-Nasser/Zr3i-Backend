using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Indexes for bulk import: batches, the uploaded file's chunks, and the staged rows.</summary>
public sealed class ImportIndexesMigration : IMigration
{
    public string Id => "0004-import-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.ImportBatches, false, ct, "TenantId", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.ImportChunks, true, ct, "TenantId", "BatchId", "Seq");
        await IndexBuilder.Ascending(database, Collections.ImportRows, true, ct, "TenantId", "BatchId", "RowNumber");
        await IndexBuilder.Ascending(database, Collections.ImportRows, false, ct, "TenantId", "BatchId", "Status");
    }
}
