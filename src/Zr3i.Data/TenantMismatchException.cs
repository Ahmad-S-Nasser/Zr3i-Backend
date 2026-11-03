namespace Zr3i.Data;

/// <summary>An entity that belongs to one tenant was handed to a repository call scoped to another.</summary>
public sealed class TenantMismatchException(string entityTenantId, string scopeTenantId)
    : InvalidOperationException($"Entity belongs to tenant '{entityTenantId}' but the call is scoped to '{scopeTenantId}'.");
