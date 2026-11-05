using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Admin;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>
/// Read APIs behind Users and roles, the Audit log, Tenants and the Portfolio screen. Members and the audit log are for organisation
/// administrators only; the tenants list is the provider's; the portfolio summary needs the same right as the farms list.
/// </summary>
public static class AdminReadEndpoints
{
    public const string ReadMembers = "kernel:users:manage";
    public const string ReadAudit = "kernel:audit:manage";   // admins only (UX guide): the "manage" action is not matched by the read-only or read-write roles
    public const string ReadTenants = "platform:tenants:read";
    public const string ReadPortfolio = "portfolio:farms:read";

    public static void MapAdminReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/members", async (string tenantId, HttpContext http, Guard guard, MemberDirectoryService members, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ReadMembers, ct, async () =>
            {
                var d = await members.ListAsync(TenantScope.ForTenant(tenantId), ct);
                return Results.Ok(new
                {
                    members = d.Members.Select(m => new
                    {
                        userId = m.UserId, email = m.Email, language = m.Language, active = m.Active, authenticatorEnrolled = m.AuthenticatorEnrolled, lockedUntil = m.LockedUntil,
                        roles = m.Roles.Select(r => new { key = r.Key, name = r.Name }), farmScope = m.FarmScope, portfolioScope = m.PortfolioScope,
                    }),
                    invitations = d.Invitations.Select(i => new { email = i.Email, status = i.Status, expiresAt = i.ExpiresAt, roles = i.Roles.Select(r => new { key = r.Key, name = r.Name }) }),
                    roles = d.Roles.Select(r => new { key = r.Key, name = r.Name }),
                });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/audit", async (string tenantId, string? cursor, int? pageSize, string? action, string? from, string? to, HttpContext http, Guard guard, AuditReader audit, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ReadAudit, ct, async () =>
            {
                var page = await audit.ListAsync(TenantScope.ForTenant(tenantId), cursor, pageSize, action, from, to, ct);
                return Results.Ok(new
                {
                    nextCursor = page.NextCursor,
                    items = page.Items.Select(e => new
                    {
                        id = e.Id, at = e.At, actorUserId = e.ActorUserId, actorEmail = e.ActorEmail, action = e.Action, objectType = e.ObjectType, objectId = e.ObjectId, outcome = e.Outcome, purpose = e.Purpose,
                    }),
                });
            })).RequireAuthorization();

        app.MapGet("/api/platform/organisations", async (HttpContext http, Guard guard, TenantDirectoryService tenants, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, ReadTenants, ct, async () =>
                Results.Ok((await tenants.ListAsync(ct)).Select(t => new
                {
                    id = t.Id, name = t.Name, accountType = t.AccountType, modules = t.Modules, createdAt = t.CreatedAt, members = t.Members, farms = t.Farms, parcels = t.Parcels,
                })))).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/portfolio", async (string tenantId, string? governorate, HttpContext http, Guard guard, PortfolioService portfolio, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ReadPortfolio, ct, async () =>
            {
                var s = await portfolio.SummaryAsync(TenantScope.ForTenant(tenantId), governorate, ct);
                return Results.Ok(new
                {
                    farms = s.Farms, parcels = s.Parcels, areaFeddan = s.AreaFeddan, governorate = s.Governorate,
                    regions = s.Regions.Select(r => new { name = r.Name, farms = r.Farms }),
                    // A farm is banded only against a farm reference distribution, and its cohort is not defined yet.
                    bands = (object?)null, bandsReason = "farm_cohort_not_defined",
                });
            })).RequireAuthorization();
    }
}
