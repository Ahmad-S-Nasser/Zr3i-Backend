using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;

namespace Zr3i.Web.Auth;

/// <summary>
/// The permission check every organisation-scoped route makes. A refusal is recorded in the audit trail of the organisation
/// it was aimed at, whether the caller belongs to another organisation or only lacks the permission.
/// </summary>
public static class TenantAccess
{
    /// <returns>Null if allowed; otherwise the 403 response to return.</returns>
    public static async Task<IResult?> DenyUnlessAsync(
        HttpContext http, string tenantId, string permission, AuthorizationService authorization, AuditService audit, CancellationToken ct)
    {
        var userId = http.User.UserId();
        if (await authorization.IsAllowedAsync(userId, tenantId, permission, ct)) return null;

        if (!string.IsNullOrWhiteSpace(tenantId))
            await audit.RecordAsync(TenantScope.ForTenant(tenantId), userId, "access.denied", "organisation", tenantId, AuditService.Denied, permission, ct);
        return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
