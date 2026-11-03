using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record SignInRequest(string? Email, string? Password);

public sealed record VerifyRequest(string? Challenge, string? Code);

/// <summary>
/// Error responses carry a stable code (for example "invalid_credentials"); the English and Arabic wording
/// lives in the frontend's resource files. Sign-in failures never say which part was wrong.
/// </summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        // A new authenticator secret to show as a QR code while accepting an invitation. Nothing is stored.
        auth.MapGet("/totp-secret", (string? label) =>
        {
            var secret = Totp.NewSecret();
            var name = Uri.EscapeDataString(string.IsNullOrWhiteSpace(label) ? "account" : label);
            return Results.Ok(new { secret, otpauthUri = $"otpauth://totp/Zr3i:{name}?secret={secret}&issuer=Zr3i" });
        });

        auth.MapPost("/sign-in", async (SignInRequest body, SignInService signIn, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(body.Password))
                return Results.BadRequest(new { error = "invalid_request" });

            var challenge = await signIn.BeginAsync(body.Email, body.Password, ct);
            return challenge is null ? Invalid() : Results.Ok(new { challenge });
        });

        auth.MapPost("/verify", async (VerifyRequest body, SignInService signIn, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Challenge) || string.IsNullOrWhiteSpace(body.Code))
                return Results.BadRequest(new { error = "invalid_request" });

            var token = await signIn.CompleteAsync(body.Challenge, body.Code, ct);
            return token is null
                ? Invalid()
                : Results.Ok(new { token, expiresInSeconds = (int)SignInService.SessionLifetime.TotalSeconds });
        });

        app.MapPost("/api/auth/sign-out", async (HttpRequest request, SessionService sessions, CancellationToken ct) =>
        {
            var token = SessionAuthenticationHandler.BearerToken(request);
            if (token is not null) await sessions.RevokeAsync(token, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static IResult Invalid() => Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
}
