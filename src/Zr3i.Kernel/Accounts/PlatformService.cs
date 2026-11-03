using Zr3i.Data;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel.Accounts;

/// <summary>Sets up the Zr3i provider organisation and gives it a first administrator.</summary>
public sealed class PlatformService(KernelStore store, OrganisationService organisations, InvitationService invitations, TimeProvider clock)
{
    /// <summary>
    /// Makes sure the provider organisation exists and, if it has no members and no live invitation for this address,
    /// invites the address as its administrator. Returns the invitation token, or null when nothing needed doing.
    /// The token is shown once to whoever runs the setup; it is not stored in clear.
    /// </summary>
    public async Task<string?> BootstrapAsync(string adminEmail, CancellationToken ct = default)
    {
        await organisations.EnsureProviderAsync(ct);
        if (await store.Memberships.CountAsync(PlatformTenant.Scope, _ => true, ct) > 0) return null;

        var email = adminEmail.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;
        var live = await store.Invitations.CountAsync(PlatformTenant.Scope, i => i.Email == email && i.AcceptedAt == null && i.ExpiresAt > now, ct);
        return live > 0 ? null : await invitations.InviteAsync(PlatformTenant.Scope, "system", email, ["org-admin"], ct);
    }
}
