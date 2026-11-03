using Zr3i.Data;

namespace Zr3i.Kernel.Audit;

public sealed class AuditService(KernelStore store, TimeProvider clock)
{
    public const string Ok = "ok";
    public const string Denied = "denied";

    public Task RecordAsync(
        TenantScope scope,
        string? actorUserId,
        string action,
        string objectType,
        string objectId,
        string outcome = Ok,
        string? purpose = null,
        CancellationToken ct = default) =>
        store.Audit.InsertAsync(scope, new AuditEvent
        {
            At = clock.GetUtcNow().UtcDateTime,
            ActorUserId = actorUserId,
            Action = action,
            ObjectType = objectType,
            ObjectId = objectId,
            Outcome = outcome,
            Purpose = purpose,
        }, ct);
}
