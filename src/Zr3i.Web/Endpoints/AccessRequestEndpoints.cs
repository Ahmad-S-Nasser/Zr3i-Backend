using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record AccessRequestBody(
    string? AccountType, string? OrganisationName, string? ContactName, string? Email, string? Purpose, string? Language,
    Dictionary<string, string?>? Fields, string? InvitedByOrganisationId);

public sealed record RejectBody(string? Reason);

/// <summary>
/// Request access (public) and the Zr3i administrators' queue (platform permission). The form's fields come from the account type
/// preset, so a new type needs no change here.
/// </summary>
public static class AccessRequestEndpoints
{
    public const string Manage = "platform:access-requests:manage";

    public static void MapAccessRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/account-types", async (AccountTypeService accountTypes, CancellationToken ct) =>
            Results.Ok((await accountTypes.ListAsync(ct)).Where(t => t.SelfSignup).Select(t => new
            {
                key = t.Key,
                labelEn = t.LabelEn,
                labelAr = t.LabelAr,
                signupFields = t.SignupFields.Select(f => new { key = f.Key, labelEn = f.LabelEn, labelAr = f.LabelAr, required = f.Required }),
            }))).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        app.MapPost("/api/access-requests", async (AccessRequestBody b, AccessRequestService service, CancellationToken ct) =>
        {
            try
            {
                var r = await service.SubmitAsync(new SubmitAccessRequest(
                    b.AccountType ?? string.Empty, b.OrganisationName ?? string.Empty, b.ContactName ?? string.Empty, b.Email ?? string.Empty,
                    b.Purpose ?? string.Empty, b.Language ?? string.Empty, b.Fields, b.InvitedByOrganisationId), ct);
                return Results.Created((string?)null, new { id = r.Id, status = r.Status });
            }
            catch (ValidationFailedException e)
            {
                return Results.BadRequest(new { error = e.Codes[0], codes = e.Codes });
            }
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        var queue = app.MapGroup("/api/platform/access-requests").RequireAuthorization();

        queue.MapGet("", async (string? status, HttpContext http, Guard guard, AccessRequestService service, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () => Results.Ok((await service.ListAsync(status, ct)).Select(Dto))));

        queue.MapPost("{id}/approve", async (string id, HttpContext http, Guard guard, AccessRequestService service, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                // No email is sent yet, so the administrator receives the invitation link token to pass on.
                var (request, token) = await service.ApproveAsync(http.User.UserId(), id, ct);
                return Results.Ok(new { request = Dto(request), inviteToken = token });
            }));

        queue.MapPost("{id}/reject", async (string id, RejectBody b, HttpContext http, Guard guard, AccessRequestService service, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () => Results.Ok(Dto(await service.RejectAsync(http.User.UserId(), id, b.Reason, ct)))));
    }

    private static object Dto(AccessRequest r) => new
    {
        id = r.Id,
        status = r.Status,
        accountType = r.AccountType,
        organisationName = r.OrganisationName,
        contactName = r.ContactName,
        email = r.Email,
        purpose = r.Purpose,
        language = r.Language,
        fields = r.Fields,
        invitedByOrganisationId = r.InvitedByOrganisationId,
        createdAt = r.CreatedAt,
        decidedAt = r.DecidedAt,
        decisionReason = r.DecisionReason,
        organisationId = r.OrganisationId,
    };
}
