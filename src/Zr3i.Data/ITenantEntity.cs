namespace Zr3i.Data;

/// <summary>Every stored document belongs to exactly one tenant (CLAUDE.md rule 1).</summary>
public interface ITenantEntity
{
    string Id { get; set; }

    string TenantId { get; set; }
}
