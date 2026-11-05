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
}
