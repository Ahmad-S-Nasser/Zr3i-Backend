using Zr3i.Data;
using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record ResetAuthenticatorBody(string? Token, string? Password, string? TotpSecret, string? TotpCode);

/// <summary>An admin resets a member's authenticator; the member re-enrols with a one-time link (like accepting an invitation, for an existing account).</summary>
public static class AuthenticatorResetEndpoints
{
    public const string ManageUsers = "kernel:users:manage";

    public static void MapAuthenticatorResetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organisations/{tenantId}/members/{userId}/reset-authenticator", async (string tenantId, string userId, HttpContext http, Guard guard, AuthenticatorResetService resets, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ManageUsers, ct, async () =>
            {
                var link = await resets.RequestAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), userId, ct);
                return Results.Ok(new { token = link.Token, expiresAt = link.ExpiresAt });
            })).RequireAuthorization();

        // Anonymous by design: the person cannot sign in. The one-time link, their password and a code from the new authenticator are the credentials.
        app.MapPost("/api/authenticator-reset", async (ResetAuthenticatorBody b, AuthenticatorResetService resets, CancellationToken ct) =>
        {
            if (b.Token is null || b.Password is null || b.TotpSecret is null || b.TotpCode is null) return Results.BadRequest(new { error = "invalid_request" });
            try
            {
                await resets.CompleteAsync(b.Token, b.Password, b.TotpSecret, b.TotpCode, ct);
                return Results.NoContent();
            }
            catch (AuthenticatorResetException e)
            {
                return e.Reason == ResetReason.BadCode ? Results.BadRequest(new { error = "bad_code" }) : Results.BadRequest(new { error = "invalid_reset" });
            }
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }
}
