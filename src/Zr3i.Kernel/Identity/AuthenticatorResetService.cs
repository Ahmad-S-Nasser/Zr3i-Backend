using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Identity;

/// <summary>Why a reset link could not be used. Every reason gets the same message; the reason is for logs and tests.</summary>
public sealed class AuthenticatorResetException(string reason) : Exception("The reset link cannot be used.")
{
    public string Reason { get; } = reason;
}

public static class ResetReason
{
    public const string Malformed = "malformed";
    public const string NotFound = "not_found";
    public const string Expired = "expired";
    public const string WrongPassword = "wrong_password";
    public const string BadCode = "bad_code";
}

public sealed record AuthenticatorResetLink(string Token, DateTime ExpiresAt);

/// <summary>
/// Lets an organisation admin reset a member's authenticator (a lost or replaced phone). The member's sessions end at once and they cannot
/// sign in until they re-enrol with a one-time link, proving who they are with their password and a code from the new authenticator.
/// Both steps are audited. The link is the credential, so only its hash is stored.
/// </summary>
public sealed class AuthenticatorResetService(KernelStore store, PasswordHasher hasher, AuditService audit, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <exception cref="ValidationFailedException">cannot_reset_self, member_in_other_organisation.</exception>
    /// <exception cref="NotFoundException">member (not in this organisation, or not active).</exception>
    public async Task<AuthenticatorResetLink> RequestAsync(TenantScope scope, string actorUserId, string targetUserId, CancellationToken ct = default)
    {
        var membership = (await store.Memberships.FindAsync(scope, m => m.UserId == targetUserId && m.IsActive, 1, ct)).FirstOrDefault() ?? throw new NotFoundException("member", targetUserId);
        var user = await store.Users.GetAsync(PlatformTenant.Scope, membership.UserId, ct);
        if (user is null || !user.IsActive) throw new NotFoundException("member", targetUserId);
        if (user.Id == actorUserId) throw new ValidationFailedException("cannot_reset_self");

        // One person has one authenticator for every organisation they belong to, so another organisation's admin must not be able to
        // take it away from them. A person who belongs to more than this organisation is left to the platform admin.
        if (user.OrganisationIds.Any(o => o != scope.TenantId)) throw new ValidationFailedException("member_in_other_organisation");

        var now = clock.GetUtcNow().UtcDateTime;
        var secret = Tokens.NewSecret();
        user.NeedsAuthenticator = true;
        user.ResetTokenHash = Tokens.Hash(secret);
        user.ResetExpiresAt = now + Lifetime;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await store.Users.ReplaceAsync(PlatformTenant.Scope, user, ct);

        foreach (var session in await store.Sessions.FindAsync(PlatformTenant.Scope, s => s.UserId == user.Id && s.RevokedAt == null, 1000, ct))
        {
            session.RevokedAt = now;
            await store.Sessions.ReplaceAsync(PlatformTenant.Scope, session, ct);
        }

        await audit.RecordAsync(scope, actorUserId, "authenticator.reset_requested", "user", user.Id, ct: ct);
        return new AuthenticatorResetLink($"{user.Id}.{secret}", user.ResetExpiresAt.Value);
    }

    /// <summary>The member re-enrols. The link works once; a wrong password or code leaves it usable until it expires.</summary>
    /// <exception cref="AuthenticatorResetException">malformed, not_found, expired, wrong_password, bad_code.</exception>
    public async Task CompleteAsync(string token, string password, string totpSecret, string totpCode, CancellationToken ct = default)
    {
        if (!Tokens.TrySplit(token, out var userId, out var secret) || string.IsNullOrWhiteSpace(userId)) throw new AuthenticatorResetException(ResetReason.Malformed);

        var user = await store.Users.GetAsync(PlatformTenant.Scope, userId, ct);
        if (user is null || !user.IsActive || !user.NeedsAuthenticator || user.ResetTokenHash is null || !Tokens.HashMatches(user.ResetTokenHash, secret))
        {
            // The same work as a real attempt, so the response time does not say whether the link was ever real.
            hasher.Verify(password ?? string.Empty, user?.PasswordHash ?? hasher.Hash("decoy-password-never-used"));
            throw new AuthenticatorResetException(ResetReason.NotFound);
        }

        var now = clock.GetUtcNow();
        if (user.ResetExpiresAt is not { } expires || expires <= now.UtcDateTime) throw new AuthenticatorResetException(ResetReason.Expired);
        if (!hasher.Verify(password ?? string.Empty, user.PasswordHash)) throw new AuthenticatorResetException(ResetReason.WrongPassword);
        if (string.IsNullOrWhiteSpace(totpSecret) || !Totp.Verify(totpSecret, totpCode ?? string.Empty, now)) throw new AuthenticatorResetException(ResetReason.BadCode);

        user.TotpSecret = totpSecret.ToUpperInvariant();
        user.NeedsAuthenticator = false;
        user.ResetTokenHash = null;
        user.ResetExpiresAt = null;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await store.Users.ReplaceAsync(PlatformTenant.Scope, user, ct);

        foreach (var organisationId in user.OrganisationIds)
            await audit.RecordAsync(TenantScope.ForTenant(organisationId), user.Id, "authenticator.reset_completed", "user", user.Id, ct: ct);
    }
}
