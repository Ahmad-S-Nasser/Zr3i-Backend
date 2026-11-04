using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Findings;

public sealed record AssessPayload(string Season);

/// <summary>Queues the assessment of every declared parcel-season of an organisation for one season. The work itself runs in the worker.</summary>
public sealed class AssessmentPlanner(KernelStore store, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const string JobType = "verification.assess";

    /// <exception cref="ValidationFailedException">invalid_season, no_parcel_seasons.</exception>
    public async Task<string> PlanAsync(TenantScope scope, string actorUserId, string season, CancellationToken ct = default)
    {
        var s = season?.Trim() ?? string.Empty;
        if (!Farms.FarmRules.IsValidSeason(s)) throw new ValidationFailedException("invalid_season");
        var count = await store.ParcelSeasons.CountAsync(scope, p => p.Season == s, ct);
        if (count == 0) throw new ValidationFailedException("no_parcel_seasons");

        var job = await jobs.EnqueueAsync(scope, JobType, JobJson.Serialize(new AssessPayload(s)), actorUserId, clock.GetUtcNow().UtcDateTime, ct: ct);
        await audit.RecordAsync(scope, actorUserId, "verification.assess_requested", "season", s, purpose: $"{count} parcel-seasons", ct: ct);
        return job.Id;
    }
}
