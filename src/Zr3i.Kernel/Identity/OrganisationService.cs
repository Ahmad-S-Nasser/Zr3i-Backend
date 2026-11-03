using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Identity;

public sealed class OrganisationService(KernelStore store, AuditService audit, TimeProvider clock)
{
    /// <summary>Creates an organisation (a tenant) and copies the built-in role templates into it.</summary>
    public async Task<Organisation> CreateAsync(string name, string accountType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountType);

        var org = new Organisation
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            AccountType = accountType.Trim(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        var scope = TenantScope.ForTenant(org.Id);

        await store.Organisations.InsertAsync(scope, org, ct);
        foreach (var t in RoleTemplates.All)
            await store.Roles.InsertAsync(scope, new Role { Key = t.Key, Name = t.Name, Permissions = [.. t.Permissions] }, ct);

        await audit.RecordAsync(scope, "system", "organisation.created", "organisation", org.Id, ct: ct);
        return org;
    }
}
