using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Consent;

public sealed record NewFarmer(string Name, string? Phone = null, string Language = "en", string? ExternalId = null, string? AccountOrganisationId = null);

public sealed class FarmerService(KernelStore store, AuditService audit, TimeProvider clock)
{
    /// <exception cref="ValidationFailedException">name_required, invalid_language, duplicate_external_id, unknown_organisation.</exception>
    public async Task<Farmer> CreateAsync(TenantScope scope, string actorUserId, NewFarmer request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors.Add("name_required");
        else if (name.Length > 200) errors.Add("name_too_long");
        if (request.Language is not ("en" or "ar")) errors.Add("invalid_language");
        var account = string.IsNullOrWhiteSpace(request.AccountOrganisationId) ? null : request.AccountOrganisationId.Trim();
        if (account is not null && await store.Organisations.GetAsync(TenantScope.ForTenant(account), account, ct) is null) errors.Add("unknown_organisation");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var farmer = new Farmer
        {
            Name = name,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Language = request.Language,
            ExternalId = string.IsNullOrWhiteSpace(request.ExternalId) ? null : request.ExternalId.Trim(),
            AccountOrganisationId = account,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        try
        {
            await store.Farmers.InsertAsync(scope, farmer, ct);
        }
        catch (DuplicateKeyException)
        {
            throw new ValidationFailedException("duplicate_external_id");
        }

        await audit.RecordAsync(scope, actorUserId, "farmer.created", "farmer", farmer.Id, ct: ct);
        return farmer;
    }

    public async Task<IReadOnlyList<Farmer>> ListAsync(TenantScope scope, CancellationToken ct = default) =>
        await store.Farmers.FindAsync(scope, _ => true, ct: ct);
}
