using Zr3i.Data;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel.Notifications;

/// <summary>Stable codes. The screens turn them into English or Arabic sentences; nothing here is prose.</summary>
public static class NotificationCodes
{
    public const string ReportReady = "report.ready";
    public const string ReportFailed = "report.failed";
    public const string ImportValidated = "import.validated";
    public const string ImportRejected = "import.rejected";
    public const string ImportCommitted = "import.committed";
    public const string ConsentRequested = "consent.requested";
    public const string ConsentGranted = "consent.granted";
    public const string ConsentDeclined = "consent.declined";
    public const string ConsentWithdrawn = "consent.withdrawn";
    public const string AuthenticatorResetCompleted = "authenticator.reset_completed";
    public const string AccountLocked = "account.locked";
}

/// <summary>
/// One line in one person's inbox. It lives in the organisation the person is in and names only a code, an object and a short detail
/// (a report kind, a purpose the person already holds). It never copies data from the object, so it is safe to show after access ends.
/// </summary>
public sealed class Notification : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

/// <summary>
/// Per-person notifications. A person lists and marks only their own rows. Writing one is best effort: a notification that cannot be
/// stored must never fail the report, import or consent change that caused it.
/// </summary>
public sealed class NotificationService(KernelStore store, AuthorizationService authorization, TimeProvider clock)
{
    public const int MaxListed = 50;
    public const string AdminPermission = "kernel:users:manage";

    /// <summary>Tells these people (members of <paramref name="scope"/>'s organisation) about something. Duplicates and blanks are ignored.</summary>
    public async Task NotifyAsync(TenantScope scope, IEnumerable<string?> userIds, string code, string objectType, string objectId, string? detail = null, CancellationToken ct = default)
    {
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var rows = userIds.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!).Distinct(StringComparer.Ordinal)
                .Select(u => new Notification { UserId = u, Code = code, ObjectType = objectType, ObjectId = objectId, Detail = Clip(detail), CreatedAt = now }).ToList();
            if (rows.Count > 0) await store.Notifications.InsertManyAsync(scope, rows, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Best effort by design (see the class comment).
        }
    }

    /// <summary>Tells the organisation's admins: the active members whose roles allow managing users.</summary>
    public async Task NotifyAdminsAsync(TenantScope scope, string code, string objectType, string objectId, string? detail = null, CancellationToken ct = default)
    {
        try
        {
            var admins = new List<string>();
            foreach (var m in await store.Memberships.FindAsync(scope, x => x.IsActive, 1000, ct))
                if ((await authorization.GetAccessAsync(m.UserId, scope.TenantId, ct))?.Allows(AdminPermission) == true) admins.Add(m.UserId);
            await NotifyAsync(scope, admins, code, objectType, objectId, detail, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Best effort.
        }
    }

    /// <summary>Tells every active member of the organisation (for example, a farmer's own account people about a consent request).</summary>
    public async Task NotifyMembersAsync(TenantScope scope, string code, string objectType, string objectId, string? detail = null, CancellationToken ct = default)
    {
        try
        {
            var members = (await store.Memberships.FindAsync(scope, x => x.IsActive, 1000, ct)).Select(m => m.UserId);
            await NotifyAsync(scope, members, code, objectType, objectId, detail, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Best effort.
        }
    }

    /// <summary>The person's own notifications, newest first.</summary>
    public async Task<IReadOnlyList<Notification>> ListAsync(TenantScope scope, string userId, bool unreadOnly, CancellationToken ct = default) =>
        await store.Notifications.FindOrderedAsync(scope, n => n.UserId == userId && (!unreadOnly || n.ReadAt == null), n => n.CreatedAt, descending: true, limit: MaxListed, ct: ct);

    public async Task<long> UnreadCountAsync(TenantScope scope, string userId, CancellationToken ct = default) =>
        await store.Notifications.CountAsync(scope, n => n.UserId == userId && n.ReadAt == null, ct);

    /// <summary>Marks some of the person's notifications read (or all of them). Ids that are not theirs are ignored. Returns how many were marked.</summary>
    public async Task<int> MarkReadAsync(TenantScope scope, string userId, IReadOnlyList<string>? ids, bool all, CancellationToken ct = default)
    {
        var wanted = ids?.ToHashSet(StringComparer.Ordinal) ?? [];
        var rows = (await store.Notifications.FindAsync(scope, n => n.UserId == userId && n.ReadAt == null, 1000, ct)).Where(n => all || wanted.Contains(n.Id)).ToList();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var n in rows)
        {
            n.ReadAt = now;
            await store.Notifications.ReplaceAsync(scope, n, ct);
        }

        return rows.Count;
    }

    private static string? Clip(string? detail) => string.IsNullOrWhiteSpace(detail) ? null : detail.Length <= 120 ? detail : detail[..120];
}
