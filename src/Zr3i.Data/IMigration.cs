using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>A one-way database change (indexes, backfills). Ids sort in run order, e.g. "0001-create-audit-indexes".</summary>
public interface IMigration
{
    string Id { get; }

    Task UpAsync(IMongoDatabase database, CancellationToken ct);
}
