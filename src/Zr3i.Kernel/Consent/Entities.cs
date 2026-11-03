using Zr3i.Data;

namespace Zr3i.Kernel.Consent;

/// <summary>
/// The person whose consent governs a set of farms. Held in the tenant that holds the farm records (a managing company, or the
/// farmer's own organisation). <see cref="AccountOrganisationId"/> is the farmer's own account, if they have one: the people in
/// that organisation, and only they, can grant and withdraw this farmer's consent in the app.
/// </summary>
public sealed class Farmer : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string Language { get; set; } = "en";
    public string? ExternalId { get; set; }
    public string? AccountOrganisationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConsentRequestStatus
{
    public const string Pending = "pending";
    public const string Granted = "granted";
    public const string Declined = "declined";
}

/// <summary>
/// "Please ask this farmer to share this data with that organisation for this purpose until that date." Raised by whoever holds the
/// farm records; it does nothing until the farmer answers, through a one-page link or in their account.
/// </summary>
public sealed class ConsentRequest : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string FarmerId { get; set; } = string.Empty;
    public string GranteeTenantId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public List<string> ParcelIds { get; set; } = [];
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Status { get; set; } = ConsentRequestStatus.Pending;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? GrantId { get; set; }
}

public static class ConsentEventTypes
{
    public const string Granted = "granted";
    public const string Withdrawn = "withdrawn";
}

public static class ConsentChannels
{
    public const string Account = "account";
    public const string Link = "link";
}

/// <summary>What a grant shares. Shown to the farmer before they agree, in their language.</summary>
public static class ConsentDataCategories
{
    public static readonly IReadOnlyList<string> All = ["parcel_boundaries", "declared_crop", "measured_area", "verification_findings", "risk_results"];
}

/// <summary>
/// One line of the consent ledger (append-only). The authoritative copy lives in the data owner's tenant (<see cref="Mirror"/> false);
/// copies are written to the grantee's tenant and to the farmer's own account so each can see the grant. A grant is the fold of its
/// events: granted, then possibly withdrawn. Expiry needs no event: it is the end of the period.
/// </summary>
public sealed class ConsentEvent : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GrantId { get; set; } = string.Empty;
    public string OwnerTenantId { get; set; } = string.Empty;
    public string FarmerId { get; set; } = string.Empty;
    public string GranteeTenantId { get; set; } = string.Empty;
    public string Event { get; set; } = ConsentEventTypes.Granted;
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Empty means every parcel of the farmer's farms at the time of reading.</summary>
    public List<string> ParcelIds { get; set; } = [];
    public List<string> DataCategories { get; set; } = [];
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public DateTime At { get; set; }
    public string Channel { get; set; } = ConsentChannels.Account;
    public string? ActorUserId { get; set; }
    public string Language { get; set; } = "en";

    /// <summary>Only on the owner's copy of a link-granted grant: the hash of the secret in the farmer's withdraw link.</summary>
    public string? WithdrawTokenHash { get; set; }
    public bool Mirror { get; set; }
}
