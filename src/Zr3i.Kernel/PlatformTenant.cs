using Zr3i.Data;

namespace Zr3i.Kernel;

/// <summary>
/// The reserved tenant that owns records about people rather than about an organisation: users, sign-in
/// challenges, sessions, and the audit trail of sign-in. Keeps "every collection has tenantId" true.
/// </summary>
public static class PlatformTenant
{
    public const string Id = "_platform";

    public static TenantScope Scope => TenantScope.ForTenant(Id);
}
