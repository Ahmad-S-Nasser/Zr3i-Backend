using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>A person's notifications are listed newest first.</summary>
public sealed class NotificationIndexesMigration : IMigration
{
    public string Id => "0014-notification-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.Notifications, false, ct, "TenantId", "UserId", "CreatedAt");
    }
}
