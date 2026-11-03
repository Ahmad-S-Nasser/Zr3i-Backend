using MongoDB.Bson;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Runs pending migrations in id order, once each. A record is written as "started" before a migration runs and
/// "applied" after. A migration that fails, or a second runner that meets a "started" record, stops with an error
/// instead of running it twice (no transactions yet: local Mongo is not a replica set).
/// </summary>
public sealed class MigrationRunner
{
    public const string CollectionName = "_migrations";

    private readonly IMongoDatabase _database;
    private readonly IReadOnlyList<IMigration> _migrations;

    public MigrationRunner(IMongoDatabase database, IEnumerable<IMigration> migrations)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _migrations = [.. (migrations ?? throw new ArgumentNullException(nameof(migrations))).OrderBy(m => m.Id, StringComparer.Ordinal)];

        var duplicate = _migrations.GroupBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate migration id '{duplicate.Key}'.", nameof(migrations));
    }

    /// <returns>The ids applied in this run, in order.</returns>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var records = _database.GetCollection<BsonDocument>(CollectionName);
        var applied = new List<string>();

        foreach (var migration in _migrations)
        {
            var existing = await records.Find(new BsonDocument("_id", migration.Id)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing["status"].AsString == "applied") continue;
                throw new MigrationInterruptedException(migration.Id);
            }

            try
            {
                await records.InsertOneAsync(
                    new BsonDocument { { "_id", migration.Id }, { "status", "started" }, { "startedAt", DateTime.UtcNow } },
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new MigrationInterruptedException(migration.Id);
            }

            await migration.UpAsync(_database, ct).ConfigureAwait(false);

            await records.UpdateOneAsync(
                new BsonDocument("_id", migration.Id),
                Builders<BsonDocument>.Update.Set("status", "applied").Set("appliedAt", DateTime.UtcNow),
                cancellationToken: ct).ConfigureAwait(false);
            applied.Add(migration.Id);
        }

        return applied;
    }
}
