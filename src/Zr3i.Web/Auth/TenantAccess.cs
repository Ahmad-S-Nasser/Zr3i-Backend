using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel;
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

        // Provider oversight (ADR 0020): a read by the Zr3i provider into any organisation. Never a write; always audited, in both places.
        if (await authorization.OversightAllowedAsync(userId, tenantId, permission, IsRead(http), ct))
        {
            await RecordOversightAsync(audit, userId, tenantId, permission, ct);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
            await audit.RecordAsync(TenantScope.ForTenant(tenantId), userId, "access.denied", "organisation", tenantId, AuditService.Denied, permission, ct);
        return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }

    public static bool IsRead(HttpContext http) => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

    /// <summary>One event in the viewed organisation's own log (it can see the provider looked) and one in the platform log. Actor, organisation and kind of page, never the data.</summary>
    public static async Task RecordOversightAsync(AuditService audit, string userId, string tenantId, string permission, CancellationToken ct)
    {
        await audit.RecordAsync(TenantScope.ForTenant(tenantId), userId, "oversight.read", "organisation", tenantId, purpose: permission, ct: ct);
        await audit.RecordAsync(PlatformTenant.Scope, userId, "oversight.read", "organisation", tenantId, purpose: permission, ct: ct);
    }
}
