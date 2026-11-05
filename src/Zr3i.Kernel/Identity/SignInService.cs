using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Identity;

/// <summary>
/// Two-step sign-in: email and password give a short-lived challenge; the six-digit code turns it into a session.
/// Every refusal returns null and looks the same to the caller, whatever the reason (unknown email, wrong password,
/// locked, deactivated, wrong code). Five failures in a row lock the account for 15 minutes.
/// </summary>
public sealed class SignInService(KernelStore store, PasswordHasher hasher, AuditService audit, TimeProvider clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    // Verified against when the account does not exist or is locked, so the response time does not reveal which.
    private readonly string _decoyHash = hasher.Hash("decoy-password-never-used");

    public async Task<string?> BeginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        var found = await store.Users.FindAsync(PlatformTenant.Scope, u => u.Email == normalised, 1, ct);
        var user = found.Count == 1 ? found[0] : null;
        var now = clock.GetUtcNow().UtcDateTime;

        if (user is null)
        {
            hasher.Verify(password ?? string.Empty, _decoyHash);
            await Deny(null, "signin.password", "unknown", ct);
            return null;
        }

        // A person whose authenticator was reset gets the same answer as a wrong password until they have re-enrolled.
        if (!user.IsActive || user.NeedsAuthenticator || (user.LockedUntil is { } until && until > now))
        {
            hasher.Verify(password ?? string.Empty, _decoyHash);
            await Deny(user.Id, "signin.password", user.Id, ct);
            return null;
        }

        if (!hasher.Verify(password ?? string.Empty, user.PasswordHash))
        {
            await RegisterFailureAsync(user, "signin.password", ct);
            return null;
        }

        var secret = Tokens.NewSecret();
        var challenge = new LoginChallenge
        {
            UserId = user.Id,
            TokenHash = Tokens.Hash(secret),
            ExpiresAt = now + ChallengeLifetime,
        };
        await store.LoginChallenges.InsertAsync(PlatformTenant.Scope, challenge, ct);
        return $"{challenge.Id}.{secret}";
    }

    public async Task<string?> CompleteAsync(string challengeToken, string code, CancellationToken ct = default)
    {
        if (!Tokens.TrySplit(challengeToken, out var challengeId, out var secret)) return null;
        var now = clock.GetUtcNow().UtcDateTime;

        var challenge = await store.LoginChallenges.GetAsync(PlatformTenant.Scope, challengeId, ct);
        if (challenge is null || challenge.Used || challenge.ExpiresAt <= now || !Tokens.HashMatches(challenge.TokenHash, secret))
            return null;

        var user = await store.Users.GetAsync(PlatformTenant.Scope, challenge.UserId, ct);
        if (user is null || !user.IsActive || user.NeedsAuthenticator || (user.LockedUntil is { } until && until > now))
        {
            await Deny(user?.Id, "signin.totp", challenge.UserId, ct);
            return null;
        }

        if (!Totp.Verify(user.TotpSecret, code, clock.GetUtcNow()))
        {
            await RegisterFailureAsync(user, "signin.totp", ct);
            return null;
        }

        challenge.Used = true;
        await store.LoginChallenges.ReplaceAsync(PlatformTenant.Scope, challenge, ct);

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await store.Users.ReplaceAsync(PlatformTenant.Scope, user, ct);

        var sessionSecret = Tokens.NewSecret();
        var session = new Session
        {
            UserId = user.Id,
            TokenHash = Tokens.Hash(sessionSecret),
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
        };
        await store.Sessions.InsertAsync(PlatformTenant.Scope, session, ct);
        await audit.RecordAsync(PlatformTenant.Scope, user.Id, "signin.completed", "session", session.Id, ct: ct);
        return $"{session.Id}.{sessionSecret}";
    }

    private async Task RegisterFailureAsync(User user, string action, CancellationToken ct)
    {
        user.FailedAttempts++;
        var locked = user.FailedAttempts >= MaxFailures;
        if (locked)
        {
            user.LockedUntil = clock.GetUtcNow().UtcDateTime + LockDuration;
            user.FailedAttempts = 0;
        }

        await store.Users.ReplaceAsync(PlatformTenant.Scope, user, ct);
        await Deny(user.Id, action, user.Id, ct);

        // The plan asks to email the user on a lock. Email sending is not built yet; this event is what it will read.
        if (locked) await audit.RecordAsync(PlatformTenant.Scope, user.Id, "account.locked", "user", user.Id, AuditService.Denied, ct: ct);
    }

    private Task Deny(string? actorUserId, string action, string objectId, CancellationToken ct) =>
        audit.RecordAsync(PlatformTenant.Scope, actorUserId, action, "user", objectId, AuditService.Denied, ct: ct);
}
