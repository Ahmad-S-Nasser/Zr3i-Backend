namespace Zr3i.Data;

/// <summary>
/// The tenant a data call runs for. Every repository method takes one and adds the tenant filter.
/// A default value is not usable, so a forgotten scope fails loudly instead of matching everything.
/// </summary>
public readonly record struct TenantScope
{
    private readonly string? _tenantId;

    private TenantScope(string tenantId) => _tenantId = tenantId;

    public string TenantId =>
        _tenantId ?? throw new InvalidOperationException("TenantScope was not created with TenantScope.ForTenant.");

    public static TenantScope ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return new TenantScope(tenantId);
    }
}
