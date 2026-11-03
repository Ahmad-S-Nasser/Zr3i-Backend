using Zr3i.Data;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Consent;

namespace Zr3i.Kernel.Identity;

public sealed class OrganisationService(KernelStore store, AccountTypeService accountTypes, AuditService audit, TimeProvider clock)
{
    /// <summary>Creates an organisation (a tenant) from an account type preset: its roles and modules come from the preset.</summary>
    /// <exception cref="ValidationFailedException">"unknown_account_type".</exception>
    public async Task<Organisation> CreateAsync(string name, string accountType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var preset = await accountTypes.GetAsync(accountType?.Trim() ?? string.Empty, ct) ?? throw new ValidationFailedException("unknown_account_type");
        return await CreateFromPresetAsync(Guid.NewGuid().ToString("N"), name, preset, ct);
    }

    /// <summary>The Zr3i provider organisation. It owns the reserved platform tenant, so its staff can manage platform-level things such as access requests.</summary>
    public async Task<Organisation> EnsureProviderAsync(CancellationToken ct = default)
    {
        var existing = await store.Organisations.GetAsync(PlatformTenant.Scope, PlatformTenant.Id, ct);
        if (existing is not null) return existing;

        var preset = BuiltInAccountTypes.All.Single(t => !t.SelfSignup);
        await accountTypes.EnsureBuiltInsAsync(ct);
        try
        {
            return await CreateFromPresetAsync(PlatformTenant.Id, "Zr3i", (await accountTypes.GetAsync(preset.Key, ct))!, ct);
        }
        catch (DuplicateKeyException)
        {
            return (await store.Organisations.GetAsync(PlatformTenant.Scope, PlatformTenant.Id, ct))!;
        }
    }

    private async Task<Organisation> CreateFromPresetAsync(string id, string name, AccountType preset, CancellationToken ct)
    {
        var org = new Organisation
        {
            Id = id,
            Name = name.Trim(),
            AccountType = preset.Key,
            Modules = [.. preset.Modules],
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        var scope = TenantScope.ForTenant(org.Id);

        await store.Organisations.InsertAsync(scope, org, ct);
        foreach (var t in RoleTemplates.All.Where(t => preset.RoleKeys.Contains(t.Key)))
            await store.Roles.InsertAsync(scope, new Role { Key = t.Key, Name = t.Name, Permissions = [.. t.Permissions] }, ct);

        if (preset.CreatesFarmerProfile)
            await store.Farmers.InsertAsync(scope, new Farmer { Name = org.Name, AccountOrganisationId = org.Id, CreatedAt = org.CreatedAt }, ct);

        await audit.RecordAsync(scope, "system", "organisation.created", "organisation", org.Id, ct: ct);
        return org;
    }
}
