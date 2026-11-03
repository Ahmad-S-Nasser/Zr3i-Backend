using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Identity;

/// <summary>Raised for every way an invitation can be unusable. The message is deliberately the same for all of them.</summary>
public sealed class InvitationException(string reason) : Exception("The invitation cannot be used.")
{
    /// <summary>For logs and tests only; never shown to the person.</summary>
    public string Reason { get; } = reason;
}

public sealed class InvitationService(KernelStore store, PasswordHasher hasher, AuditService audit, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly string[] Languages = ["en", "ar"];

    /// <summary>
    /// Creates an invitation and returns the token to put in the email link. The token carries the tenant id,
    /// so accepting it never needs a lookup across tenants. Only the token's hash is stored.
    /// </summary>
    public async Task<string> InviteAsync(
        TenantScope scope, string invitedByUserId, string email, IReadOnlyList<string> roleKeys, CancellationToken ct = default)
    {
        var normalised = NormaliseEmail(email);
        if (roleKeys.Count == 0) throw new InvitationException("no roles");

        var roles = await store.Roles.FindAsync(scope, _ => true, ct: ct);
        var roleIds = new List<string>();
        foreach (var key in roleKeys)
        {
            var role = roles.FirstOrDefault(r => r.Key == key) ?? throw new InvitationException("unknown role");
            roleIds.Add(role.Id);
        }

        var secret = Tokens.NewSecret();
        var invitation = new Invitation
        {
            Email = normalised,
            TokenHash = Tokens.Hash(secret),
            RoleIds = roleIds,
            InvitedBy = invitedByUserId,
            ExpiresAt = clock.GetUtcNow().UtcDateTime + Lifetime,
        };
        await store.Invitations.InsertAsync(scope, invitation, ct);
        await audit.RecordAsync(scope, invitedByUserId, "invitation.created", "invitation", invitation.Id, ct: ct);
        return $"{scope.TenantId}.{secret}";
    }

    /// <summary>A new person accepts: sets a password, proves their authenticator works, picks a language.</summary>
    public async Task<User> AcceptAsync(
        string token, string password, string language, string totpSecret, string totpCode, CancellationToken ct = default)
    {
        var (scope, invitation) = await FindUsableAsync(token, ct);

        if (!PasswordHasher.MeetsPolicy(password)) throw new InvitationException("weak password");
        if (!Languages.Contains(language)) throw new InvitationException("language");
        if (!Totp.Verify(totpSecret, totpCode, clock.GetUtcNow())) throw new InvitationException("bad code");

        var existing = await store.Users.FindAsync(PlatformTenant.Scope, u => u.Email == invitation.Email, 1, ct);
        if (existing.Count > 0) throw new InvitationException("existing user");

        var user = new User
        {
            Email = invitation.Email,
            PasswordHash = hasher.Hash(password),
            TotpSecret = totpSecret.ToUpperInvariant(),
            Language = language,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await store.Users.InsertAsync(PlatformTenant.Scope, user, ct);

        await JoinAsync(scope, invitation, user, ct);
        return user;
    }

    /// <summary>A signed-in person who already has an account joins another organisation. The email must match the invitation.</summary>
    public async Task AcceptAsExistingUserAsync(string token, string userId, CancellationToken ct = default)
    {
        var (scope, invitation) = await FindUsableAsync(token, ct);

        var user = await store.Users.GetAsync(PlatformTenant.Scope, userId, ct);
        if (user is null || !user.IsActive || user.Email != invitation.Email) throw new InvitationException("wrong user");

        await JoinAsync(scope, invitation, user, ct);
    }

    private async Task JoinAsync(TenantScope scope, Invitation invitation, User user, CancellationToken ct)
    {
        var already = await store.Memberships.FindAsync(scope, m => m.UserId == user.Id, 1, ct);
        if (already.Count == 0)
            await store.Memberships.InsertAsync(scope, new Membership { UserId = user.Id, RoleIds = [.. invitation.RoleIds] }, ct);

        invitation.AcceptedAt = clock.GetUtcNow().UtcDateTime;
        await store.Invitations.ReplaceAsync(scope, invitation, ct);
        await audit.RecordAsync(scope, user.Id, "invitation.accepted", "invitation", invitation.Id, ct: ct);
    }

    private async Task<(TenantScope Scope, Invitation Invitation)> FindUsableAsync(string token, CancellationToken ct)
    {
        if (!Tokens.TrySplit(token, out var tenantId, out var secret) || string.IsNullOrWhiteSpace(tenantId))
            throw new InvitationException("malformed");

        var scope = TenantScope.ForTenant(tenantId);
        var hash = Tokens.Hash(secret);
        var found = await store.Invitations.FindAsync(scope, i => i.TokenHash == hash, 1, ct);
        var invitation = found.Count == 1 ? found[0] : throw new InvitationException("not found");

        if (invitation.AcceptedAt is not null) throw new InvitationException("already used");
        if (invitation.ExpiresAt <= clock.GetUtcNow().UtcDateTime) throw new InvitationException("expired");
        return (scope, invitation);
    }

    internal static string NormaliseEmail(string email)
    {
        var e = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (e.Length < 3 || !e.Contains('@', StringComparison.Ordinal)) throw new InvitationException("email");
        return e;
    }
}
