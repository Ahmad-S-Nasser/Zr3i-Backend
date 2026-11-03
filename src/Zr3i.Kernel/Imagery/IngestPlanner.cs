using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Imagery;

public sealed record IngestPayload(string SceneId);

/// <summary>Queues the reading of catalogue scenes for an organisation's parcels: one job per scene.</summary>
public sealed class IngestPlanner(SceneCatalog catalog, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const string JobType = "imagery.ingest";
    public const int MaxScenes = 400;

    /// <summary>The grid the pixel test counts on. Scenes in another UTM zone are not read yet.</summary>
    public const int SupportedEpsg = 32636;

    /// <exception cref="ValidationFailedException">invalid_period, no_scenes, too_many_scenes.</exception>
    public async Task<IReadOnlyList<string>> PlanAsync(TenantScope scope, string actorUserId, DateTime from, DateTime to, double maxCloudCover = 100, string? tile = null, CancellationToken ct = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(400)) throw new ValidationFailedException("invalid_period");
        var scenes = (await catalog.ListAsync(from, to, tile, ct)).Where(s => s.Epsg == SupportedEpsg && s.CloudCover <= maxCloudCover).ToList();
        if (scenes.Count == 0) throw new ValidationFailedException("no_scenes");
        if (scenes.Count > MaxScenes) throw new ValidationFailedException("too_many_scenes");

        var now = clock.GetUtcNow().UtcDateTime;
        var ids = new List<string>();
        foreach (var scene in scenes)
            ids.Add((await jobs.EnqueueAsync(scope, JobType, JobJson.Serialize(new IngestPayload(scene.SceneId)), actorUserId, now, ct: ct)).Id);

        await audit.RecordAsync(scope, actorUserId, "imagery.ingest_requested", "imagery", ids[0], purpose: $"{scenes.Count} scenes", ct: ct);
        return ids;
    }
}
