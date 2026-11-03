namespace Zr3i.Kernel.Identity;

public sealed record SessionInfo(string SessionId, string UserId, DateTime ExpiresAt);

public sealed class SessionService(KernelStore store, TimeProvider clock)
{
    /// <summary>Returns the session for a token, or null if it is unknown, altered, revoked, expired or its user is inactive.</summary>
    public async Task<SessionInfo?> ValidateAsync(string token, CancellationToken ct = default)
    {
        var session = await FindAsync(token, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return null;

        var user = await store.Users.GetAsync(PlatformTenant.Scope, session.UserId, ct);
        return user is { IsActive: true } ? new SessionInfo(session.Id, session.UserId, session.ExpiresAt) : null;
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        var session = await FindAsync(token, ct);
        if (session is null || session.RevokedAt is not null) return;

        session.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await store.Sessions.ReplaceAsync(PlatformTenant.Scope, session, ct);
    }

    private async Task<Session?> FindAsync(string token, CancellationToken ct)
    {
        if (!Tokens.TrySplit(token, out var id, out var secret)) return null;
        var session = await store.Sessions.GetAsync(PlatformTenant.Scope, id, ct);
        return session is not null && Tokens.HashMatches(session.TokenHash, secret) ? session : null;
    }
}
