using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zr3i.Kernel.Identity;

namespace Zr3i.Web.Auth;

/// <summary>Reads "Authorization: Bearer {token}" and validates it against the Kernel's sessions.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";
    public const string SessionIdClaim = "sid";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = BearerToken(Request);
        if (token is null) return AuthenticateResult.NoResult();

        var session = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid session.");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, session.UserId), new Claim(SessionIdClaim, session.SessionId)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
            ? header[prefix.Length..].Trim()
            : null;
    }
}

public static class UserClaims
{
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No authenticated user.");
}
