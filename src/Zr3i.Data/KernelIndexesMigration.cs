using MongoDB.Bson;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Indexes for the Kernel collections. Every index starts with TenantId (hard rule 1).
/// Field names match the Kernel entity properties; the integration tests would catch a drift.
/// </summary>
public sealed class KernelIndexesMigration : IMigration
{
    public string Id => "0001-kernel-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await Index(database, Collections.Organisations, false, ct, "TenantId");
        await Index(database, Collections.Users, true, ct, "TenantId", "Email");
        await Index(database, Collections.Memberships, false, ct, "TenantId", "UserId");
        await Index(database, Collections.Roles, true, ct, "TenantId", "Key");
        await Index(database, Collections.Invitations, false, ct, "TenantId", "Email");
        await Index(database, Collections.Sessions, false, ct, "TenantId", "UserId");
        await Index(database, Collections.LoginChallenges, false, ct, "TenantId", "UserId");
        await Index(database, Collections.AuditEvents, false, ct, "TenantId", "At");
    }

    private static async Task Index(IMongoDatabase db, string collection, bool unique, CancellationToken ct, params string[] fields)
    {
        var keys = new BsonDocument();
        foreach (var field in fields) keys.Add(field, 1);

        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Unique = unique });
        await db.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(model, cancellationToken: ct).ConfigureAwait(false);
    }
}
