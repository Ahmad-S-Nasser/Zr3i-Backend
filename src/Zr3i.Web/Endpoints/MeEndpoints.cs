using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (HttpContext http, KernelStore store, AuthorizationService authorization, CancellationToken ct) =>
        {
            var user = await store.Users.GetAsync(PlatformTenant.Scope, http.User.UserId(), ct);
            if (user is null) return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var organisations = new List<object>();
            foreach (var organisationId in user.OrganisationIds)
            {
                var access = await authorization.GetAccessAsync(user.Id, organisationId, ct);
                var org = await store.Organisations.GetAsync(TenantScope.ForTenant(organisationId), organisationId, ct);
                if (access is null || org is null) continue;

                organisations.Add(new
                {
                    id = org.Id,
                    name = org.Name,
                    accountType = org.AccountType,
                    permissions = access.Permissions,
                    portfolioIds = access.PortfolioIds,
                    farmIds = access.FarmIds,
                });
            }

            return Results.Ok(new { id = user.Id, email = user.Email, language = user.Language, organisations });
        }).RequireAuthorization();
    }
}
