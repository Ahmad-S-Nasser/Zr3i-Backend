using Zr3i.Data;

namespace Zr3i.Kernel.Audit;

/// <summary>
/// One append-only fact: who did what to which object, when, for what purpose, with what outcome.
/// Never holds passwords, tokens or secrets.
/// </summary>
public sealed class AuditEvent : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public string? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string Outcome { get; set; } = "ok";
    public string? Purpose { get; set; }
}
