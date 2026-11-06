using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Notifications;

namespace Zr3i.Kernel.Consent;

/// <param name="ValidFrom">Defaults to now.</param>
/// <param name="ParcelIds">Empty or null means every parcel of the farmer's farms.</param>
public sealed record GrantTerms(string FarmerId, string GranteeTenantId, string Purpose, DateTime ValidUntil, IReadOnlyList<string>? ParcelIds = null, DateTime? ValidFrom = null);

/// <summary>What the farmer is shown on the one-page consent. Everything needed to decide, nothing more.</summary>
public sealed record ConsentOffer(
    string RequesterName, string GranteeName, string FarmerName, string Language, string Purpose,
    DateTime ValidFrom, DateTime ValidUntil, int? ParcelCount, IReadOnlyList<string> DataCategories, string Status);

public sealed record ConsentResponse(bool Granted, string? GrantId, string? WithdrawToken);

public static class GrantStatus
{
    public const string Active = "active";
    public const string Scheduled = "scheduled";
    public const string Expired = "expired";
    public const string Withdrawn = "withdrawn";
}

/// <summary>A grant as the fold of its ledger lines. <see cref="Role"/> is how the viewing tenant relates to it: owner, grantee or farmer.</summary>
public sealed record GrantView(
    string GrantId, string OwnerTenantId, string FarmerId, string GranteeTenantId, string Purpose, IReadOnlyList<string> ParcelIds,
    DateTime ValidFrom, DateTime ValidUntil, DateTime GrantedAt, DateTime? WithdrawnAt, string Status, string Channel, string Role);

/// <summary>
/// Farmer-owned consent (hard rule 3). The holder of the records can only ask; the farmer grants, through a one-page link or in their
/// own account, with a purpose and a period; the farmer can withdraw at any time and it takes effect on the next read. Every change
/// is a new ledger line, written in the owner's tenant and mirrored to the other parties.
/// </summary>
public sealed class ConsentService(KernelStore store, AuditService audit, TimeProvider clock, NotificationService? notify = null)
{
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(730);
    public static readonly TimeSpan MinPeriod = TimeSpan.FromDays(1);
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(14);

    /// <summary>The holder of the farm records asks a farmer to share data. Returns the request and the link token for the farmer (no message is sent yet).</summary>
    public async Task<(ConsentRequest Request, string Token)> RequestAsync(TenantScope owner, string actorUserId, GrantTerms terms, CancellationToken ct = default)
    {
        var farmer = await store.Farmers.GetAsync(owner, terms.FarmerId ?? string.Empty, ct) ?? throw new NotFoundException("farmer", terms.FarmerId ?? string.Empty);
        var clean = await CheckTermsAsync(owner, farmer, terms, ct);

        var secret = Tokens.NewSecret();
        var now = Now();
        var request = new ConsentRequest
        {
            FarmerId = farmer.Id,
            GranteeTenantId = clean.Grantee,
            Purpose = clean.Purpose,
            ParcelIds = clean.ParcelIds,
            ValidFrom = clean.ValidFrom,
            ValidUntil = terms.ValidUntil,
            TokenHash = Tokens.Hash(secret),
            ExpiresAt = now + RequestLifetime,
            RequestedBy = actorUserId,
            CreatedAt = now,
        };
        await store.ConsentRequests.InsertAsync(owner, request, ct);
        await audit.RecordAsync(owner, actorUserId, "consent.requested", "consent_request", request.Id, purpose: clean.Purpose, ct: ct);
        if (notify is not null && !string.IsNullOrWhiteSpace(farmer.AccountOrganisationId))
            await notify.NotifyMembersAsync(TenantScope.ForTenant(farmer.AccountOrganisationId), NotificationCodes.ConsentRequested, "consent_request", request.Id, clean.Purpose, ct);
        return (request, $"{owner.TenantId}.{request.Id}.{secret}");
    }

    public async Task<ConsentOffer> DescribeAsync(string token, CancellationToken ct = default)
    {
        var (request, owner, farmer) = await OpenRequestAsync(token, ct);
        var granteeName = (await store.Organisations.GetAsync(TenantScope.ForTenant(request.GranteeTenantId), request.GranteeTenantId, ct))?.Name ?? string.Empty;
        var requesterName = (await store.Organisations.GetAsync(owner, owner.TenantId, ct))?.Name ?? string.Empty;
        return new ConsentOffer(requesterName, granteeName, farmer.Name, farmer.Language, request.Purpose, request.ValidFrom, request.ValidUntil,
            request.ParcelIds.Count == 0 ? null : request.ParcelIds.Count, ConsentDataCategories.All, request.Status);
    }

    /// <summary>
    /// The farmer's answer from the one-page link. Nothing is granted unless <paramref name="accepted"/> is explicitly true; the page never
    /// pre-ticks it. A grant returns a withdraw token, shown once, so the farmer can withdraw without an account.
    /// </summary>
    public async Task<ConsentResponse> RespondAsync(string token, bool accepted, string language, CancellationToken ct = default)
    {
        if (language is not ("en" or "ar")) throw new ValidationFailedException("invalid_language");
        var (request, owner, farmer) = await OpenRequestAsync(token, ct);
        if (request.Status != ConsentRequestStatus.Pending) throw new ValidationFailedException("already_answered");
        if (request.ExpiresAt <= Now()) throw new ValidationFailedException("request_expired");

        request.DecidedAt = Now();
        if (!accepted)
        {
            request.Status = ConsentRequestStatus.Declined;
            await store.ConsentRequests.ReplaceAsync(owner, request, ct);
            await audit.RecordAsync(owner, null, "consent.declined", "consent_request", request.Id, purpose: request.Purpose, ct: ct);
            if (notify is not null) await notify.NotifyAsync(owner, [request.RequestedBy], NotificationCodes.ConsentDeclined, "consent_request", request.Id, request.Purpose, ct);
            return new ConsentResponse(false, null, null);
        }

        var withdrawSecret = Tokens.NewSecret();
        var grantId = NewId.Create();
        var terms = new GrantTerms(farmer.Id, request.GranteeTenantId, request.Purpose, request.ValidUntil, request.ParcelIds, request.ValidFrom);
        await WriteAsync(owner, farmer, Template(owner, farmer, terms, request.ValidFrom, grantId, ConsentEventTypes.Granted, ConsentChannels.Link, null, language, Tokens.Hash(withdrawSecret)), ct);

        request.Status = ConsentRequestStatus.Granted;
        request.GrantId = grantId;
        await store.ConsentRequests.ReplaceAsync(owner, request, ct);
        await audit.RecordAsync(owner, null, "consent.granted", "consent_grant", grantId, purpose: request.Purpose, ct: ct);
        if (notify is not null)
        {
            await notify.NotifyAsync(owner, [request.RequestedBy], NotificationCodes.ConsentGranted, "consent_grant", grantId, request.Purpose, ct);
            await notify.NotifyAdminsAsync(TenantScope.ForTenant(request.GranteeTenantId), NotificationCodes.ConsentGranted, "consent_grant", grantId, request.Purpose, ct);
        }
        return new ConsentResponse(true, grantId, $"{owner.TenantId}.{grantId}.{withdrawSecret}");
    }

    /// <summary>A farmer with their own account grants directly. The holder of the records cannot do this for them.</summary>
    /// <exception cref="ValidationFailedException">"not_your_consent" unless the farmer's own organisation is the one acting.</exception>
    public async Task<GrantView> GrantAsync(TenantScope actorOrg, string actorUserId, GrantTerms terms, CancellationToken ct = default)
    {
        var farmer = await store.Farmers.GetAsync(actorOrg, terms.FarmerId ?? string.Empty, ct) ?? throw new NotFoundException("farmer", terms.FarmerId ?? string.Empty);
        if (farmer.AccountOrganisationId != actorOrg.TenantId) throw new ValidationFailedException("not_your_consent");

        var clean = await CheckTermsAsync(actorOrg, farmer, terms, ct);
        var grantId = NewId.Create();
        await WriteAsync(actorOrg, farmer, Template(actorOrg, farmer, terms with { Purpose = clean.Purpose, ParcelIds = clean.ParcelIds }, clean.ValidFrom, grantId,
            ConsentEventTypes.Granted, ConsentChannels.Account, actorUserId, farmer.Language, null), ct);
        await audit.RecordAsync(actorOrg, actorUserId, "consent.granted", "consent_grant", grantId, purpose: clean.Purpose, ct: ct);
        if (notify is not null) await notify.NotifyAdminsAsync(TenantScope.ForTenant(clean.Grantee), NotificationCodes.ConsentGranted, "consent_grant", grantId, clean.Purpose, ct);
        return (await ListAsync(actorOrg, ct)).Single(g => g.GrantId == grantId);
    }

    /// <summary>
    /// The farmer withdraws from their own account. The organisation acting must be the farmer's account organisation: neither the
    /// company holding the records nor the receiving organisation can do it.
    /// </summary>
    public async Task<GrantView> WithdrawAsync(TenantScope actorOrg, string actorUserId, string grantId, CancellationToken ct = default)
    {
        var events = await store.ConsentEvents.FindAsync(actorOrg, e => e.GrantId == grantId, ct: ct);
        var first = events.FirstOrDefault(e => e.Event == ConsentEventTypes.Granted) ?? throw new NotFoundException("consent", grantId ?? string.Empty);

        var owner = TenantScope.ForTenant(first.OwnerTenantId);
        var farmer = await store.Farmers.GetAsync(owner, first.FarmerId, ct);   // the one read of the owner's farmer record
        if (farmer?.AccountOrganisationId != actorOrg.TenantId) throw new ValidationFailedException("not_your_consent");

        await WithdrawCoreAsync(owner, farmer, grantId, ConsentChannels.Account, actorUserId, farmer.Language, ct);
        return (await ListAsync(actorOrg, ct)).Single(g => g.GrantId == grantId);
    }

    /// <summary>Withdraw with the link the farmer was given when they agreed. No account needed. Withdrawing twice is harmless.</summary>
    public async Task<GrantView> WithdrawByTokenAsync(string token, CancellationToken ct = default)
    {
        if (!Tokens.TrySplit(token, out var id, out var secret) || id.Split('.') is not [var ownerId, var grantId] || string.IsNullOrWhiteSpace(ownerId))
            throw new ValidationFailedException("invalid_token");

        var owner = TenantScope.ForTenant(ownerId);
        var events = await store.ConsentEvents.FindAsync(owner, e => e.GrantId == grantId, ct: ct);
        var granted = events.FirstOrDefault(e => e.Event == ConsentEventTypes.Granted);
        if (granted?.WithdrawTokenHash is not { } hash || !Tokens.HashMatches(hash, secret)) throw new ValidationFailedException("invalid_token");

        var farmer = await store.Farmers.GetAsync(owner, granted.FarmerId, ct) ?? throw new ValidationFailedException("invalid_token");
        await WithdrawCoreAsync(owner, farmer, grantId, ConsentChannels.Link, null, granted.Language, ct);
        return Fold(await store.ConsentEvents.FindAsync(owner, e => e.GrantId == grantId, ct: ct), Now(), owner.TenantId).Single();
    }

    /// <summary>The grants this tenant can see: the ones on its own data, the ones made to it, and the ones its farmer account owns.</summary>
    public async Task<IReadOnlyList<GrantView>> ListAsync(TenantScope scope, CancellationToken ct = default) =>
        Fold(await store.ConsentEvents.FindAsync(scope, _ => true, 1000, ct), Now(), scope.TenantId);

    // --- internals ---

    private async Task WithdrawCoreAsync(TenantScope owner, Farmer farmer, string grantId, string channel, string? actor, string language, CancellationToken ct)
    {
        var ownerEvents = await store.ConsentEvents.FindAsync(owner, e => e.GrantId == grantId, ct: ct);
        if (ownerEvents.Any(e => e.Event == ConsentEventTypes.Withdrawn)) return;

        var g = ownerEvents.First(e => e.Event == ConsentEventTypes.Granted);
        var terms = new GrantTerms(g.FarmerId, g.GranteeTenantId, g.Purpose, g.ValidUntil, g.ParcelIds, g.ValidFrom);
        await WriteAsync(owner, farmer, Template(owner, farmer, terms, g.ValidFrom, grantId, ConsentEventTypes.Withdrawn, channel, actor, language, null), ct);
        await audit.RecordAsync(owner, actor, "consent.withdrawn", "consent_grant", grantId, purpose: g.Purpose, ct: ct);
        if (notify is not null)
        {
            await notify.NotifyAdminsAsync(TenantScope.ForTenant(g.GranteeTenantId), NotificationCodes.ConsentWithdrawn, "consent_grant", grantId, g.Purpose, ct);
            await notify.NotifyAdminsAsync(owner, NotificationCodes.ConsentWithdrawn, "consent_grant", grantId, g.Purpose, ct);
        }
    }

    private ConsentEvent Template(TenantScope owner, Farmer farmer, GrantTerms terms, DateTime validFrom, string grantId, string type, string channel, string? actor, string language, string? withdrawHash) =>
        new()
        {
            GrantId = grantId,
            OwnerTenantId = owner.TenantId,
            FarmerId = farmer.Id,
            GranteeTenantId = terms.GranteeTenantId,
            Event = type,
            Purpose = terms.Purpose,
            ParcelIds = [.. terms.ParcelIds ?? []],
            DataCategories = [.. ConsentDataCategories.All],
            ValidFrom = validFrom,
            ValidUntil = terms.ValidUntil,
            At = Now(),
            Channel = channel,
            ActorUserId = actor,
            Language = language,
            WithdrawTokenHash = withdrawHash,
        };

    /// <summary>Writes the owner's line and mirrors it to the grantee and the farmer's own account, so each can see (and the farmer can act on) the grant.</summary>
    private async Task WriteAsync(TenantScope owner, Farmer farmer, ConsentEvent line, CancellationToken ct)
    {
        await store.ConsentEvents.InsertAsync(owner, line, ct);
        foreach (var tenant in new[] { line.GranteeTenantId, farmer.AccountOrganisationId }.Where(t => !string.IsNullOrEmpty(t)).Distinct().Where(t => t != owner.TenantId))
        {
            var copy = new ConsentEvent
            {
                GrantId = line.GrantId, OwnerTenantId = line.OwnerTenantId, FarmerId = line.FarmerId, GranteeTenantId = line.GranteeTenantId, Event = line.Event,
                Purpose = line.Purpose, ParcelIds = [.. line.ParcelIds], DataCategories = [.. line.DataCategories], ValidFrom = line.ValidFrom, ValidUntil = line.ValidUntil,
                At = line.At, Channel = line.Channel, ActorUserId = line.ActorUserId, Language = line.Language, Mirror = true,
            };
            await store.ConsentEvents.InsertAsync(TenantScope.ForTenant(tenant!), copy, ct);
        }
    }

    private async Task<(string Grantee, string Purpose, List<string> ParcelIds, DateTime ValidFrom)> CheckTermsAsync(TenantScope owner, Farmer farmer, GrantTerms terms, CancellationToken ct)
    {
        var errors = new List<string>();
        var grantee = terms.GranteeTenantId?.Trim() ?? string.Empty;
        if (grantee.Length == 0 || await store.Organisations.GetAsync(TenantScope.ForTenant(grantee), grantee, ct) is null) errors.Add("unknown_grantee");
        else if (grantee == owner.TenantId) errors.Add("grantee_is_owner");

        var purpose = terms.Purpose?.Trim() ?? string.Empty;
        if (purpose.Length == 0) errors.Add("purpose_required");
        else if (purpose.Length > 300) errors.Add("purpose_too_long");

        var now = Now();
        var from = terms.ValidFrom ?? now;
        if (terms.ValidUntil <= now) errors.Add("period_in_past");
        else if (terms.ValidUntil - from < MinPeriod) errors.Add("period_too_short");
        else if (terms.ValidUntil - from > MaxPeriod) errors.Add("period_too_long");

        var parcelIds = (terms.ParcelIds ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (parcelIds.Count > 0)
        {
            var farms = (await store.Farms.FindAsync(owner, f => f.FarmerId == farmer.Id, ct: ct)).Select(f => f.Id).ToList();
            var owned = (await store.Parcels.FindAsync(owner, p => parcelIds.Contains(p.Id) && farms.Contains(p.FarmId), ct: ct)).Select(p => p.Id).ToHashSet();
            if (parcelIds.Any(p => !owned.Contains(p))) errors.Add("unknown_parcel");
        }

        if (errors.Count > 0) throw new ValidationFailedException(errors);
        return (grantee, purpose, parcelIds, from);
    }

    private async Task<(ConsentRequest Request, TenantScope Owner, Farmer Farmer)> OpenRequestAsync(string token, CancellationToken ct)
    {
        if (!Tokens.TrySplit(token, out var id, out var secret) || id.Split('.') is not [var ownerId, var requestId] || string.IsNullOrWhiteSpace(ownerId))
            throw new ValidationFailedException("invalid_token");

        var owner = TenantScope.ForTenant(ownerId);
        var request = await store.ConsentRequests.GetAsync(owner, requestId, ct);
        if (request is null || !Tokens.HashMatches(request.TokenHash, secret)) throw new ValidationFailedException("invalid_token");
        var farmer = await store.Farmers.GetAsync(owner, request.FarmerId, ct) ?? throw new ValidationFailedException("invalid_token");
        return (request, owner, farmer);
    }

    /// <summary>Turns ledger lines into grants. A group without a "granted" line (a late-linked account only saw the withdrawal) is skipped.</summary>
    internal static List<GrantView> Fold(IEnumerable<ConsentEvent> events, DateTime now, string viewer)
    {
        var views = new List<GrantView>();
        foreach (var group in events.GroupBy(e => e.GrantId))
        {
            var granted = group.Where(e => e.Event == ConsentEventTypes.Granted).OrderBy(e => e.At).FirstOrDefault();
            if (granted is null) continue;
            var withdrawn = group.Where(e => e.Event == ConsentEventTypes.Withdrawn).OrderBy(e => e.At).FirstOrDefault();

            var status = withdrawn is not null ? GrantStatus.Withdrawn
                : now < granted.ValidFrom ? GrantStatus.Scheduled
                : now >= granted.ValidUntil ? GrantStatus.Expired
                : GrantStatus.Active;
            var role = granted.OwnerTenantId == viewer ? "owner" : granted.GranteeTenantId == viewer ? "grantee" : "farmer";
            views.Add(new GrantView(granted.GrantId, granted.OwnerTenantId, granted.FarmerId, granted.GranteeTenantId, granted.Purpose, granted.ParcelIds,
                granted.ValidFrom, granted.ValidUntil, granted.At, withdrawn?.At, status, granted.Channel, role));
        }

        return [.. views.OrderBy(v => v.GrantedAt)];
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
