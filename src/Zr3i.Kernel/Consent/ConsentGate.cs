using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Consent;

/// <summary>Why the gate said no. The reason is a stable code; callers show it, they do not decide anything from it.</summary>
public sealed class ConsentDeniedException(string reason) : Exception("Consent does not allow this read: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// What a successful gate check allows: reads inside <see cref="OwnerScope"/>, limited to <see cref="AllowedParcelIds"/>,
/// until <see cref="ValidUntil"/>, for <see cref="Purpose"/>. Every screen that shows this data names <see cref="GrantorName"/> and the expiry.
/// </summary>
public sealed class ConsentAccess(
    TenantScope ownerScope, string grantId, string farmerId, string purpose, DateTime validUntil, string grantorName, IReadOnlyList<string> allowedParcelIds)
{
    public TenantScope OwnerScope { get; } = ownerScope;
    public string GrantId { get; } = grantId;
    public string FarmerId { get; } = farmerId;
    public string Purpose { get; } = purpose;
    public DateTime ValidUntil { get; } = validUntil;
    public string GrantorName { get; } = grantorName;
    public IReadOnlyList<string> AllowedParcelIds { get; } = allowedParcelIds;

    /// <summary>
    /// Splits a request into what may be read and a count of what may not. Parcels outside the grant are only ever counted ("withheld"),
    /// never named, and are not part of any total. With no request, everything allowed is returned.
    /// </summary>
    public (IReadOnlyList<string> Allowed, int Withheld) Resolve(IEnumerable<string>? requested)
    {
        if (requested is null) return (AllowedParcelIds, 0);
        var wanted = requested.Distinct().ToList();
        var allowed = AllowedParcelIds.ToHashSet();
        var ok = wanted.Where(allowed.Contains).ToList();
        return (ok, wanted.Count - ok.Count);
    }
}

/// <summary>
/// The only way one organisation reads another's data. It re-reads the owner's consent ledger every time (a withdrawal counts on the
/// next read), accepts only the organisation the grant names, and records the read, or the refusal, in both organisations' audit trails.
/// This is the single deliberate cross-tenant read in the Kernel besides the job claim and the consent link pages.
/// </summary>
public sealed class ConsentGate(KernelStore store, AuditService audit, TimeProvider clock)
{
    /// <exception cref="ConsentDeniedException">no_such_grant, withdrawn, expired or not_yet_valid. The refusal is audited.</exception>
    public async Task<ConsentAccess> OpenAsync(TenantScope grantee, string actorUserId, string grantId, CancellationToken ct = default)
    {
        // The grantee's own mirror tells us where the grant lives. A grant that names someone else is invisible here.
        var mirror = (await store.ConsentEvents.FindAsync(grantee, e => e.GrantId == grantId, ct: ct)).FirstOrDefault(e => e.GranteeTenantId == grantee.TenantId);
        if (mirror is null)
        {
            await audit.RecordAsync(grantee, actorUserId, "consent.refused", "consent_grant", grantId ?? string.Empty, AuditService.Denied, "no_such_grant", ct);
            throw new ConsentDeniedException("no_such_grant");
        }

        var owner = TenantScope.ForTenant(mirror.OwnerTenantId);
        var now = clock.GetUtcNow().UtcDateTime;
        var grant = ConsentService.Fold(await store.ConsentEvents.FindAsync(owner, e => e.GrantId == grantId, ct: ct), now, owner.TenantId)
            .FirstOrDefault(g => g.GranteeTenantId == grantee.TenantId);
        var reason = grant is null ? "no_such_grant"
            : grant.Status switch { GrantStatus.Withdrawn => "withdrawn", GrantStatus.Expired => "expired", GrantStatus.Scheduled => "not_yet_valid", _ => null };

        if (reason is not null)
        {
            await audit.RecordAsync(grantee, actorUserId, "consent.refused", "consent_grant", grantId!, AuditService.Denied, reason, ct);
            await audit.RecordAsync(owner, actorUserId, "consent.refused", "consent_grant", grantId!, AuditService.Denied, reason, ct);
            throw new ConsentDeniedException(reason);
        }

        var allowed = await AllowedParcelsAsync(owner, grant!, ct);
        var grantor = (await store.Organisations.GetAsync(owner, owner.TenantId, ct))?.Name ?? string.Empty;

        await audit.RecordAsync(owner, actorUserId, "consent.read", "consent_grant", grantId!, purpose: grant!.Purpose, ct: ct);
        await audit.RecordAsync(grantee, actorUserId, "consent.read", "consent_grant", grantId!, purpose: grant.Purpose, ct: ct);
        return new ConsentAccess(owner, grant.GrantId, grant.FarmerId, grant.Purpose, grant.ValidUntil, grantor, allowed);
    }

    /// <summary>The parcels the grant covers right now: the farmer's parcels, narrowed to the listed ones if the grant lists any.</summary>
    private async Task<IReadOnlyList<string>> AllowedParcelsAsync(TenantScope owner, GrantView grant, CancellationToken ct)
    {
        var farmIds = (await store.Farms.FindAsync(owner, f => f.FarmerId == grant.FarmerId, ct: ct)).Select(f => f.Id).ToList();
        if (farmIds.Count == 0) return [];

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(owner, p => farmIds.Contains(p.FarmId), cursor, 1000, ct);
            ids.AddRange(page.Items.Select(p => p.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return grant.ParcelIds.Count == 0 ? ids : [.. ids.Where(grant.ParcelIds.Contains)];
    }
}
