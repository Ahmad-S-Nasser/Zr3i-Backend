using Zr3i.Data;

namespace Zr3i.Kernel.Identity;

/// <summary>An organisation is a tenant: its TenantId is its own Id. AccountType is data (a preset key), never a code path.</summary>
public sealed class Organisation : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public List<string> Modules { get; set; } = [];
    public DateTime CreatedAt { get; set; }

    /// <summary>A synthetic namespace holds illustrative data and no client data. Screens and reports say so on every page.</summary>
    public bool IsSynthetic { get; set; }
}

/// <summary>
/// A person. One user can belong to several organisations, so users are not owned by one of them:
/// they live in the reserved <see cref="PlatformTenant"/>. TotpSecret is stored as-is for now;
/// encrypting it at rest needs a key from configuration before any pilot data (see ADR 0002).
/// </summary>
public sealed class User : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string TotpSecret { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public List<string> OrganisationIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Set when an organisation admin has reset this person's authenticator. They cannot sign in until they re-enrol with the one-time
    /// link (its hash and expiry are kept here, never the link itself).
    /// </summary>
    public bool NeedsAuthenticator { get; set; }
    public string? ResetTokenHash { get; set; }
    public DateTime? ResetExpiresAt { get; set; }
}

/// <summary>A user's place in one organisation. Empty PortfolioIds and FarmIds mean no scope limit.</summary>
public sealed class Membership : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public List<string> RoleIds { get; set; } = [];
    public List<string> PortfolioIds { get; set; } = [];
    public List<string> FarmIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

public sealed class Role : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}

public sealed class Invitation : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public List<string> RoleIds { get; set; } = [];
    public string InvitedBy { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public sealed class Session : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>The short-lived result of the password step. It is exchanged for a session by the one-time code step.</summary>
public sealed class LoginChallenge : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
}
