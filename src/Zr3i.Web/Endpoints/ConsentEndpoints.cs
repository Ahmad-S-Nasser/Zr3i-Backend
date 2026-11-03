using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record FarmerBody(string? Name, string? Phone, string? Language, string? ExternalId, string? AccountOrganisationId);

public sealed record GrantBody(string? FarmerId, string? GranteeTenantId, string? Purpose, DateTime? ValidUntil, string[]? ParcelIds, DateTime? ValidFrom);

public sealed record RespondBody(bool? Accepted, string? Language);

/// <summary>
/// Farmers and consent. Consent belongs to the farmer: a company can ask, only the farmer (in their account, or from the link they
/// were given) can agree, and withdrawing is always open to them. A receiving organisation reads another's data only through
/// <see cref="SharedDataService"/>, which goes through the consent gate.
/// </summary>
public static class ConsentEndpoints
{
    public const string FarmersRead = "kernel:farmers:read";
    public const string FarmersWrite = "kernel:farmers:write";
    public const string ConsentsRead = "kernel:consents:read";
    public const string ConsentsRequest = "kernel:consents:write";
    public const string ConsentsManage = "kernel:consents:manage";
    public const string SharedRead = "kernel:shared:read";

    public static void MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var org = app.MapGroup("/api/organisations/{tenantId}").RequireAuthorization();

        org.MapPost("farmers", async (string tenantId, FarmerBody b, HttpContext http, Guard guard, FarmerService farmers, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, FarmersWrite, ct, async () =>
            {
                var f = await farmers.CreateAsync(TenantScope.ForTenant(tenantId), http.User.UserId(),
                    new NewFarmer(b.Name ?? string.Empty, b.Phone, b.Language ?? "en", b.ExternalId, b.AccountOrganisationId), ct);
                return Results.Created((string?)null, FarmerDto(f));
            }));

        org.MapGet("farmers", async (string tenantId, HttpContext http, Guard guard, FarmerService farmers, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, FarmersRead, ct, async () => Results.Ok((await farmers.ListAsync(TenantScope.ForTenant(tenantId), ct)).Select(FarmerDto))));

        // The holder of the records asks. The response carries the link to give the farmer (no message is sent yet).
        org.MapPost("consent-requests", async (string tenantId, GrantBody b, HttpContext http, Guard guard, ConsentService consent, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ConsentsRequest, ct, async () =>
            {
                var (request, token) = await consent.RequestAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), Terms(b), ct);
                return Results.Created((string?)null, new { id = request.Id, consentToken = token, expiresAt = request.ExpiresAt });
            }));

        org.MapGet("consents", async (string tenantId, HttpContext http, Guard guard, ConsentService consent, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ConsentsRead, ct, async () => Results.Ok((await consent.ListAsync(TenantScope.ForTenant(tenantId), ct)).Select(GrantDto))));

        org.MapPost("consents", async (string tenantId, GrantBody b, HttpContext http, Guard guard, ConsentService consent, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ConsentsManage, ct, async () =>
                Results.Created((string?)null, GrantDto(await consent.GrantAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), Terms(b), ct)))));

        org.MapPost("consents/{grantId}/withdraw", async (string tenantId, string grantId, HttpContext http, Guard guard, ConsentService consent, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ConsentsManage, ct, async () =>
                Results.Ok(GrantDto(await consent.WithdrawAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), grantId, ct)))));

        // The receiving side: what has been shared with this organisation, and reading it through the gate.
        org.MapGet("shared", async (string tenantId, HttpContext http, Guard guard, ConsentService consent, KernelStore store, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, SharedRead, ct, async () =>
            {
                var grants = (await consent.ListAsync(TenantScope.ForTenant(tenantId), ct)).Where(g => g.Role == "grantee" && g.Status == GrantStatus.Active).ToList();
                var result = new List<object>();
                foreach (var g in grants)
                {
                    var grantor = (await store.Organisations.GetAsync(TenantScope.ForTenant(g.OwnerTenantId), g.OwnerTenantId, ct))?.Name ?? string.Empty;
                    result.Add(new { grantId = g.GrantId, grantor, purpose = g.Purpose, validUntil = g.ValidUntil, parcelCount = g.ParcelIds.Count == 0 ? (int?)null : g.ParcelIds.Count });
                }

                return Results.Ok(result);
            }));

        org.MapGet("shared/{grantId}/parcels", async (string tenantId, string grantId, string? ids, HttpContext http, Guard guard, SharedDataService shared, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, SharedRead, ct, async () =>
            {
                string[]? requested = string.IsNullOrWhiteSpace(ids) ? null : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                try
                {
                    var read = await shared.ReadParcelsAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), grantId, requested, ct);
                    return Results.Ok(new
                    {
                        grantor = read.GrantorName,
                        purpose = read.Purpose,
                        validUntil = read.ValidUntil,
                        parcels = read.Parcels.Select(p => new { id = p.Id, label = p.Label, areaFeddan = p.AreaFeddan }),
                        withheld = read.Withheld,
                    });
                }
                catch (ConsentDeniedException e)
                {
                    return Results.Json(new { error = "consent_denied", reason = e.Reason }, statusCode: StatusCodes.Status403Forbidden);
                }
            }));

        // The farmer's one-page consent. No sign-in: the link is the credential, and it works once.
        var link = app.MapGroup("/api/consent").RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        link.MapGet("{token}", async (string token, ConsentService consent, CancellationToken ct) =>
            await Public(async () =>
            {
                var o = await consent.DescribeAsync(token, ct);
                return Results.Ok(new
                {
                    requester = o.RequesterName, grantee = o.GranteeName, farmer = o.FarmerName, language = o.Language, purpose = o.Purpose,
                    validFrom = o.ValidFrom, validUntil = o.ValidUntil, parcelCount = o.ParcelCount, dataCategories = o.DataCategories, status = o.Status,
                });
            }));

        // "accepted" must be present and true. A missing value is a refusal, never a yes.
        link.MapPost("{token}", async (string token, RespondBody b, ConsentService consent, CancellationToken ct) =>
            await Public(async () =>
            {
                var r = await consent.RespondAsync(token, b.Accepted == true, b.Language ?? string.Empty, ct);
                return Results.Ok(new { granted = r.Granted, grantId = r.GrantId, withdrawToken = r.WithdrawToken });
            }));

        link.MapPost("withdraw/{token}", async (string token, ConsentService consent, CancellationToken ct) =>
            await Public(async () => Results.Ok(GrantDto(await consent.WithdrawByTokenAsync(token, ct)))));
    }

    private static async Task<IResult> Public(Func<Task<IResult>> body)
    {
        try
        {
            return await body();
        }
        catch (ValidationFailedException e)
        {
            return Results.BadRequest(new { error = e.Codes[0], codes = e.Codes });
        }
    }

    private static GrantTerms Terms(GrantBody b) =>
        new(b.FarmerId ?? string.Empty, b.GranteeTenantId ?? string.Empty, b.Purpose ?? string.Empty, b.ValidUntil ?? default, b.ParcelIds, b.ValidFrom);

    private static object FarmerDto(Farmer f) => new { id = f.Id, name = f.Name, phone = f.Phone, language = f.Language, externalId = f.ExternalId, accountOrganisationId = f.AccountOrganisationId };

    private static object GrantDto(GrantView g) => new
    {
        grantId = g.GrantId, role = g.Role, status = g.Status, farmerId = g.FarmerId, granteeTenantId = g.GranteeTenantId, purpose = g.Purpose,
        parcelIds = g.ParcelIds, validFrom = g.ValidFrom, validUntil = g.ValidUntil, grantedAt = g.GrantedAt, withdrawnAt = g.WithdrawnAt, channel = g.Channel,
    };
}
