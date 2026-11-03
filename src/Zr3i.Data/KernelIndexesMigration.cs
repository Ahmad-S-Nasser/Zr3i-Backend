using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Indexes for the identity collections. Field names match the Kernel entity properties;
/// the integration tests would catch a drift.
/// </summary>
public sealed class KernelIndexesMigration : IMigration
{
    public string Id => "0001-kernel-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Organisations, false, ct, "TenantId");
        await IndexBuilder.Ascending(database, Collections.Users, true, ct, "TenantId", "Email");
        await IndexBuilder.Ascending(database, Collections.Memberships, false, ct, "TenantId", "UserId");
        await IndexBuilder.Ascending(database, Collections.Roles, true, ct, "TenantId", "Key");
        await IndexBuilder.Ascending(database, Collections.Invitations, false, ct, "TenantId", "Email");
        await IndexBuilder.Ascending(database, Collections.Sessions, false, ct, "TenantId", "UserId");
        await IndexBuilder.Ascending(database, Collections.LoginChallenges, false, ct, "TenantId", "UserId");
        await IndexBuilder.Ascending(database, Collections.AuditEvents, false, ct, "TenantId", "At");
    }
}
