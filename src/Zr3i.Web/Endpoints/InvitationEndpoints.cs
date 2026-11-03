using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record AcceptInvitationRequest(string? Token, string? Password, string? Language, string? TotpSecret, string? TotpCode);

public sealed record AcceptExistingRequest(string? Token);

public sealed record InviteRequest(string? Email, string[]? RoleKeys);

public static class InvitationEndpoints
{
    public const string ManageUsers = "kernel:users:manage";

    public static void MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: the person has no account yet. The token is the credential.
        app.MapPost("/api/invitations/accept", async (AcceptInvitationRequest b, InvitationService invitations, CancellationToken ct) =>
        {
            if (b.Token is null || b.Password is null || b.Language is null || b.TotpSecret is null || b.TotpCode is null)
                return Results.BadRequest(new { error = "invalid_request" });
            try
            {
                var user = await invitations.AcceptAsync(b.Token, b.Password, b.Language, b.TotpSecret, b.TotpCode, ct);
                return Results.Created($"/api/me", new { userId = user.Id });
            }
            catch (InvitationException e)
            {
                return Failure(e);
            }
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        app.MapPost("/api/invitations/accept-existing", async (AcceptExistingRequest b, HttpContext http, InvitationService invitations, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(b.Token)) return Results.BadRequest(new { error = "invalid_request" });
            try
            {
                await invitations.AcceptAsExistingUserAsync(b.Token, http.User.UserId(), ct);
                return Results.NoContent();
            }
            catch (InvitationException e)
            {
                return Failure(e);
            }
        }).RequireAuthorization().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        app.MapPost("/api/organisations/{tenantId}/invitations", async (
            string tenantId, InviteRequest b, HttpContext http,
            AuthorizationService authorization, InvitationService invitations, AuditService audit, CancellationToken ct) =>
        {
            if (await TenantAccess.DenyUnlessAsync(http, tenantId, ManageUsers, authorization, audit, ct) is { } denied) return denied;
            var userId = http.User.UserId();

            if (string.IsNullOrWhiteSpace(b.Email) || b.RoleKeys is null) return Results.BadRequest(new { error = "invalid_request" });
            try
            {
                // No email is sent yet, so the inviter gets the link token to pass on.
                var token = await invitations.InviteAsync(TenantScope.ForTenant(tenantId), userId, b.Email, b.RoleKeys, ct);
                return Results.Created((string?)null, new { inviteToken = token });
            }
            catch (InvitationException e)
            {
                return Failure(e);
            }
        }).RequireAuthorization();
    }

    /// <summary>
    /// Only reasons the token holder can act on are named. Anything that would reveal whether a token exists
    /// (not found, expired, used, malformed) collapses into one code.
    /// </summary>
    private static IResult Failure(InvitationException e) => e.Reason switch
    {
        InvitationReason.WeakPassword => Results.BadRequest(new { error = "weak_password" }),
        InvitationReason.BadCode => Results.BadRequest(new { error = "bad_code" }),
        InvitationReason.Language => Results.BadRequest(new { error = "invalid_request" }),
        InvitationReason.UnknownRole => Results.BadRequest(new { error = "unknown_role" }),
        InvitationReason.NoRoles => Results.BadRequest(new { error = "invalid_request" }),
        InvitationReason.Email => Results.BadRequest(new { error = "invalid_email" }),
        InvitationReason.ExistingUser => Results.Json(new { error = "account_exists" }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(new { error = "invitation_invalid" }),
    };
}
