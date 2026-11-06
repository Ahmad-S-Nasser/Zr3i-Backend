using Zr3i.Data;

namespace Zr3i.Kernel.Identity;

/// <summary>What a user may do in one organisation. Empty scope lists mean the membership is not limited.</summary>
/// <summary>A role the user holds in an organisation.</summary>
public sealed record RoleRef(string Key, string Name);

public sealed record Access(IReadOnlyList<string> Permissions, IReadOnlyList<string> PortfolioIds, IReadOnlyList<string> FarmIds, IReadOnlyList<RoleRef>? Roles = null)
{
    public bool Allows(string required) => Permissions.Any(p => Permission.Matches(p, required));
}

public sealed class AuthorizationService(KernelStore store)
{
    /// <summary>Null if the user is inactive or has no active membership in the organisation.</summary>
    public async Task<Access?> GetAccessAsync(string userId, string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(tenantId)) return null;

        var user = await store.Users.GetAsync(PlatformTenant.Scope, userId, ct);
        if (user is not { IsActive: true }) return null;

        var scope = TenantScope.ForTenant(tenantId);
        var memberships = await store.Memberships.FindAsync(scope, m => m.UserId == userId && m.IsActive, ct: ct);
        if (memberships.Count == 0) return null;

        var roleIds = memberships.SelectMany(m => m.RoleIds).Distinct().ToList();
        var roles = await store.Roles.FindAsync(scope, r => roleIds.Contains(r.Id), ct: ct);

        return new Access(
            [.. roles.SelectMany(r => r.Permissions).Distinct()],
            [.. memberships.SelectMany(m => m.PortfolioIds).Distinct()],
            [.. memberships.SelectMany(m => m.FarmIds).Distinct()],
            [.. roles.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new RoleRef(r.Key, r.Name))]);
    }

    public async Task<bool> IsAllowedAsync(string userId, string tenantId, string permission, CancellationToken ct = default) =>
        (await GetAccessAsync(userId, tenantId, ct))?.Allows(permission) ?? false;

    /// <summary>The permission that lets the Zr3i provider read any organisation (ADR 0020).</summary>
    public const string OversightPermission = "platform:oversight:read";

    /// <summary>Who may see usage across organisations. Held the same way as oversight.</summary>
    public const string UsagePermission = "platform:usage:read";

    /// <summary>
    /// Provider oversight: may this user READ this organisation although they are not a member? Only for a read request (GET or HEAD), only where
    /// the route's permission is a read or an admin-only "manage" read, only for an organisation that exists, and only for a provider member who holds
    /// the oversight permission itself or the full wildcard of the provider's org-admin. A partial wildcard such as an analyst's "*:*:read" is not enough.
    /// Nothing here lets anyone write. The caller records the access in the audit trail.
    /// </summary>
    public async Task<bool> OversightAllowedAsync(string userId, string tenantId, string permission, bool isReadRequest, CancellationToken ct = default)
    {
        if (!isReadRequest || string.IsNullOrWhiteSpace(tenantId) || tenantId == PlatformTenant.Id) return false;
        var parts = permission.Split(':');
        if (parts.Length != 3 || parts[2] is not ("read" or "manage")) return false;
        if (!await HoldsPlatformPermissionAsync(userId, OversightPermission, ct)) return false;
        return await store.Organisations.GetAsync(TenantScope.ForTenant(tenantId), tenantId, ct) is not null;
    }

    /// <summary>True if the user is a provider member holding exactly this platform permission, or the full wildcard. Partial wildcards do not count.</summary>
    public async Task<bool> HoldsPlatformPermissionAsync(string userId, string permission, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(userId, PlatformTenant.Id, ct);
        return access is not null && access.Permissions.Any(p => p == permission || p == "*:*:*");
    }
}
