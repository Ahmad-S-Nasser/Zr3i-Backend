using Zr3i.Data;

namespace Zr3i.Kernel.Audit;

public sealed class AuditService(KernelStore store, TimeProvider clock)
{
    public const string Ok = "ok";
    public const string Denied = "denied";

    /// <summary>Builds an event stamped with the clock, for callers that write many at once with <see cref="RecordManyAsync"/>.</summary>
    public AuditEvent Create(string? actorUserId, string action, string objectType, string objectId, string outcome = Ok, string? purpose = null) =>
        new()
        {
            At = clock.GetUtcNow().UtcDateTime,
            ActorUserId = actorUserId,
            Action = action,
            ObjectType = objectType,
            ObjectId = objectId,
            Outcome = outcome,
            Purpose = purpose,
        };

    public Task RecordManyAsync(TenantScope scope, IReadOnlyList<AuditEvent> events, CancellationToken ct = default) =>
        store.Audit.InsertManyAsync(scope, events, ct);

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
