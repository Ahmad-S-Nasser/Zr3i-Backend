using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (HttpContext http, KernelStore store, AuthorizationService authorization, AccountTypeService accountTypes, CancellationToken ct) =>
        {
            var user = await store.Users.GetAsync(PlatformTenant.Scope, http.User.UserId(), ct);
            if (user is null) return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var organisations = new List<object>();
            foreach (var organisationId in user.OrganisationIds)
            {
                var access = await authorization.GetAccessAsync(user.Id, organisationId, ct);
                var org = await store.Organisations.GetAsync(TenantScope.ForTenant(organisationId), organisationId, ct);
                if (access is null || org is null) continue;
                var preset = await accountTypes.GetAsync(org.AccountType, ct);

                organisations.Add(new
                {
                    id = org.Id,
                    name = org.Name,
                    accountType = org.AccountType,
                    isSynthetic = org.IsSynthetic,

                    // The account type's preset (data, not code): what this organisation sees and where it lands. Keys are stable ids the frontend maps to screens.
                    accountTypeLabelEn = preset?.LabelEn, accountTypeLabelAr = preset?.LabelAr, homeView = preset?.HomeView,
                    navigation = preset?.Navigation ?? [], modules = preset?.Modules ?? [],
                    roles = (access.Roles ?? []).Select(r => new { key = r.Key, name = r.Name }),
                    permissions = access.Permissions,
                    portfolioIds = access.PortfolioIds,
                    farmIds = access.FarmIds,
                });
            }

            return Results.Ok(new { id = user.Id, email = user.Email, language = user.Language, organisations });
        }).RequireAuthorization();
    }
}
