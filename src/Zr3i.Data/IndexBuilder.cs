using MongoDB.Bson;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Index creation shared by the migrations. Every index starts with TenantId (hard rule 1).</summary>
internal static class IndexBuilder
{
    public static Task Ascending(IMongoDatabase db, string collection, bool unique, CancellationToken ct, params string[] fields) =>
        Create(db, collection, Keys(fields), new CreateIndexOptions { Unique = unique }, ct);

    /// <summary>Unique among rows that have an external id; rows without one (null) never collide.</summary>
    public static Task UniqueExternalId(IMongoDatabase db, string collection, CancellationToken ct) =>
        Create(db, collection, Keys("TenantId", "ExternalId"),
            new CreateIndexOptions<BsonDocument>
            {
                Unique = true,
                PartialFilterExpression = new BsonDocument("ExternalId", new BsonDocument("$type", "string")),
            }, ct);

    public static Task Geo(IMongoDatabase db, string collection, string geometryField, CancellationToken ct) =>
        Create(db, collection, new BsonDocument { { "TenantId", 1 }, { geometryField, "2dsphere" } }, new CreateIndexOptions(), ct);

    private static BsonDocument Keys(params string[] fields)
    {
        var keys = new BsonDocument();
        foreach (var field in fields) keys.Add(field, 1);
        return keys;
    }

    private static Task Create(IMongoDatabase db, string collection, BsonDocument keys, CreateIndexOptions options, CancellationToken ct) =>
        db.GetCollection<BsonDocument>(collection).Indexes
            .CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, options), cancellationToken: ct);
}
