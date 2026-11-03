using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel.Accounts;

public static class AccessRequestStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>A visitor's request for an account. It lives in the platform tenant until a Zr3i administrator decides it.</summary>
public sealed class AccessRequest : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string OrganisationName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Language { get; set; } = "en";

    /// <summary>The answers to the preset's sign-up fields (for example the commercial register number).</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>A farmer may name the company or authority that invited them.</summary>
    public string? InvitedByOrganisationId { get; set; }
    public string Status { get; set; } = AccessRequestStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public string? OrganisationId { get; set; }
}

public sealed record SubmitAccessRequest(
    string AccountType,
    string OrganisationName,
    string ContactName,
    string Email,
    string Purpose,
    string Language,
    IReadOnlyDictionary<string, string?>? Fields = null,
    string? InvitedByOrganisationId = null);

/// <summary>
/// Sign-up by account type. Nothing here knows about any particular type: the preset says whether sign-up is open and which
/// fields it asks for. A request is decided by a Zr3i administrator; approval creates the organisation from its preset and
/// issues the invitation for its first admin.
/// </summary>
public sealed class AccessRequestService(
    KernelStore store, AccountTypeService accountTypes, OrganisationService organisations, InvitationService invitations, AuditService audit, TimeProvider clock)
{
    private const int MaxLength = 200;
    private const int MaxPurpose = 500;

    /// <exception cref="ValidationFailedException">Every problem found, as stable codes.</exception>
    public async Task<AccessRequest> SubmitAsync(SubmitAccessRequest request, CancellationToken ct = default)
    {
        var preset = await accountTypes.GetAsync(request.AccountType?.Trim() ?? string.Empty, ct) ?? throw new ValidationFailedException("unknown_account_type");
        if (!preset.SelfSignup) throw new ValidationFailedException("signup_not_allowed");

        var errors = new List<string>();
        var name = Text(request.OrganisationName, MaxLength, "organisation_name_required", errors);
        var contact = Text(request.ContactName, MaxLength, "contact_name_required", errors);
        var purpose = Text(request.Purpose, MaxPurpose, "purpose_required", errors);
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length is < 3 or > 254 || !email.Contains('@', StringComparison.Ordinal) || email.Any(char.IsWhiteSpace)) errors.Add("invalid_email");
        if (request.Language is not ("en" or "ar")) errors.Add("invalid_language");

        var given = (request.Fields ?? new Dictionary<string, string?>()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value!.Trim());
        foreach (var key in (request.Fields ?? new Dictionary<string, string?>()).Keys.Where(k => preset.SignupFields.All(f => f.Key != k))) errors.Add($"unknown_field:{key}");
        foreach (var field in preset.SignupFields.Where(f => f.Required && !given.ContainsKey(f.Key))) errors.Add($"field_required:{field.Key}");

        var inviter = string.IsNullOrWhiteSpace(request.InvitedByOrganisationId) ? null : request.InvitedByOrganisationId.Trim();
        if (inviter is not null && await store.Organisations.GetAsync(TenantScope.ForTenant(inviter), inviter, ct) is null) errors.Add("unknown_inviter");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var type = preset.Key;
        if (await store.AccessRequests.CountAsync(PlatformTenant.Scope, r => r.Email == email && r.AccountType == type && r.Status == AccessRequestStatus.Pending, ct) > 0)
            throw new ValidationFailedException("duplicate_pending");

        var row = new AccessRequest
        {
            AccountType = type,
            OrganisationName = name,
            ContactName = contact,
            Email = email,
            Purpose = purpose,
            Language = request.Language,
            Fields = given,
            InvitedByOrganisationId = inviter,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await store.AccessRequests.InsertAsync(PlatformTenant.Scope, row, ct);
        await audit.RecordAsync(PlatformTenant.Scope, null, "access_request.created", "access_request", row.Id, ct: ct);
        return row;
    }

    public async Task<IReadOnlyList<AccessRequest>> ListAsync(string? status, CancellationToken ct = default) =>
        [.. (status is null
            ? await store.AccessRequests.FindAsync(PlatformTenant.Scope, _ => true, ct: ct)
            : await store.AccessRequests.FindAsync(PlatformTenant.Scope, r => r.Status == status, ct: ct)).OrderBy(r => r.CreatedAt)];

    /// <returns>The decided request and the invitation token for the new organisation's first admin (no email is sent yet).</returns>
    public async Task<(AccessRequest Request, string InviteToken)> ApproveAsync(string actorUserId, string requestId, CancellationToken ct = default)
    {
        var request = await PendingAsync(requestId, ct);
        var org = await organisations.CreateAsync(request.OrganisationName, request.AccountType, ct);
        var token = await invitations.InviteAsync(TenantScope.ForTenant(org.Id), actorUserId, request.Email, ["org-admin"], ct);

        request.Status = AccessRequestStatus.Approved;
        request.OrganisationId = org.Id;
        await Decide(request, actorUserId, "access_request.approved", ct);
        return (request, token);
    }

    public async Task<AccessRequest> RejectAsync(string actorUserId, string requestId, string? reason, CancellationToken ct = default)
    {
        var request = await PendingAsync(requestId, ct);
        request.Status = AccessRequestStatus.Rejected;
        request.DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 300)];
        await Decide(request, actorUserId, "access_request.rejected", ct);
        return request;
    }

    private async Task<AccessRequest> PendingAsync(string requestId, CancellationToken ct)
    {
        var request = await store.AccessRequests.GetAsync(PlatformTenant.Scope, requestId ?? string.Empty, ct) ?? throw new NotFoundException("access_request", requestId ?? string.Empty);
        return request.Status == AccessRequestStatus.Pending ? request : throw new ValidationFailedException("not_pending");
    }

    private async Task Decide(AccessRequest request, string actorUserId, string action, CancellationToken ct)
    {
        request.DecidedBy = actorUserId;
        request.DecidedAt = clock.GetUtcNow().UtcDateTime;
        await store.AccessRequests.ReplaceAsync(PlatformTenant.Scope, request, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actorUserId, action, "access_request", request.Id, ct: ct);
    }

    private static string Text(string? value, int max, string code, List<string> errors)
    {
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0) errors.Add(code);
        else if (v.Length > max) errors.Add(code.Replace("_required", "_too_long", StringComparison.Ordinal));
        return v;
    }
}
