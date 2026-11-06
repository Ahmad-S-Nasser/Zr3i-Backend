using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Admin;
using Zr3i.Kernel.Identity;
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
                    accountTypeLabelEn = t.AccountTypeLabelEn, accountTypeLabelAr = t.AccountTypeLabelAr, homeView = t.HomeView, navigation = t.Navigation ?? [], isSynthetic = t.IsSynthetic,
                })))).RequireAuthorization();

        // Usage across organisations, for revenue share (ADR 0020). Counts only. The provider's full permission is required; a partial wildcard is not enough.
        app.MapGet("/api/platform/usage", async (DateTime? from, DateTime? to, string? format, HttpContext http, Guard guard, AuthorizationService authorization, PlatformUsageService usage, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, ReadTenants, ct, async () =>
            {
                if (!await authorization.HoldsPlatformPermissionAsync(http.User.UserId(), AuthorizationService.UsagePermission, ct)) return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
                if (from is null || to is null) return Results.BadRequest(new { error = "invalid_period" });
                var u = await usage.UsageAsync(from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), ct);
                if (format == "csv")
                    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(PlatformUsageService.ToCsv(u))).ToArray(), "text/csv; charset=utf-8", $"usage-{u.From:yyyy-MM-dd}.csv");

                static object Flat(UsageNumbers n) => new
                {
                    parcels = n.Parcels, areaFeddan = n.AreaFeddan, readings = n.Readings, usableReadings = n.UsableReadings, scenes = n.Scenes, pictures = n.Pictures, pictureBytes = n.PictureBytes,
                    reports = n.Reports, downloads = n.Downloads, importBatches = n.ImportBatches, importedRows = n.ImportedRows, grants = n.Grants, withdrawals = n.Withdrawals, sharedReads = n.SharedReads,
                };
                return Results.Ok(new
                {
                    from = u.From, to = u.To, totals = Flat(u.Totals),
                    organisations = u.Organisations.Select(r => new
                    {
                        id = r.Id, name = r.Name, accountType = r.AccountType, parcels = r.Numbers.Parcels, areaFeddan = r.Numbers.AreaFeddan, readings = r.Numbers.Readings, usableReadings = r.Numbers.UsableReadings,
                        scenes = r.Numbers.Scenes, pictures = r.Numbers.Pictures, pictureBytes = r.Numbers.PictureBytes, reports = r.Numbers.Reports, downloads = r.Numbers.Downloads,
                        importBatches = r.Numbers.ImportBatches, importedRows = r.Numbers.ImportedRows, grants = r.Numbers.Grants, withdrawals = r.Numbers.Withdrawals, sharedReads = r.Numbers.SharedReads,
                    }),
                });
            })).RequireAuthorization();

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
