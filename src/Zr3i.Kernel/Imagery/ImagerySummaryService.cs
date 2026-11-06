using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Chips;
using Zr3i.Kernel.Farms;

namespace Zr3i.Kernel.Imagery;

/// <summary>Counts of this organisation's jobs of one kind, and the error codes of the ones that failed (never their payloads).</summary>
public sealed record JobCounts(int Queued, int Running, int Succeeded, int Failed, IReadOnlyList<string> ErrorCodes);

public sealed record ImageryStatus(JobCounts Discover, JobCounts Ingest, JobCounts Chip);

/// <summary>A catalogue scene as this organisation sees it: how many of its parcels lie under it and how many were read.</summary>
public sealed record SceneRow(Scene Scene, int ParcelsUnder, int Observed, int Readable);

public sealed record SceneOverview(IReadOnlyList<SceneRow> Scenes, bool HasParcels, int Total);

/// <summary>
/// What the Satellite screen shows: whether searches and readings are running, and which catalogue scenes cover this organisation's parcels.
/// Scenes are public facts; the counts come only from this organisation's own parcels and observations.
/// </summary>
public sealed class ImagerySummaryService(KernelStore store, JobQueue jobs, SceneCatalog catalog)
{
    public const int MaxScenes = 100;
    private const int Chunk = 500;
    private const int MaxParcels = 20000;

    public async Task<ImageryStatus> StatusAsync(TenantScope scope, CancellationToken ct = default) =>
        new(await CountsAsync(scope, DiscoveryPlanner.JobType, ct), await CountsAsync(scope, IngestPlanner.JobType, ct), await CountsAsync(scope, ChipPlanner.JobType, ct));

    private async Task<JobCounts> CountsAsync(TenantScope scope, string type, CancellationToken ct)
    {
        var all = await jobs.ListAsync(scope, type, 1000, ct);
        return new JobCounts(
            all.Count(j => j.Status == JobStatus.Queued), all.Count(j => j.Status == JobStatus.Running), all.Count(j => j.Status == JobStatus.Succeeded),
            all.Count(j => j.Status == JobStatus.Failed), [.. all.Where(j => j.Status == JobStatus.Failed && j.ErrorCode is not null).Select(j => j.ErrorCode!).Distinct().Order(StringComparer.Ordinal)]);
    }

    /// <exception cref="ValidationFailedException">invalid_period.</exception>
    public async Task<SceneOverview> ScenesAsync(TenantScope scope, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(400)) throw new ValidationFailedException("invalid_period");
        var scenes = await catalog.ListAsync(from, to, null, ct);

        var boxes = await ParcelBoxesAsync(scope, ct);
        if (boxes.Count == 0)
            return new SceneOverview([.. scenes.OrderByDescending(s => s.SensingTime).Take(MaxScenes).Select(s => new SceneRow(s, 0, 0, 0))], false, scenes.Count);

        var rows = new List<SceneRow>();
        foreach (var s in scenes.OrderByDescending(s => s.SensingTime).ThenBy(s => s.SceneId, StringComparer.Ordinal))
        {
            var under = boxes.Count(b => b.MinLon <= s.MaxLon && b.MaxLon >= s.MinLon && b.MinLat <= s.MaxLat && b.MaxLat >= s.MinLat);
            if (under == 0) continue;
            var observed = (int)await store.ParcelObservations.CountAsync(scope, o => o.SceneId == s.SceneId, ct);
            var readable = (int)await store.ParcelObservations.CountAsync(scope, o => o.SceneId == s.SceneId && o.Readable, ct);
            rows.Add(new SceneRow(s, under, observed, readable));
        }

        return new SceneOverview([.. rows.Take(MaxScenes)], true, rows.Count);
    }

    private async Task<List<BoundaryVersion>> ParcelBoxesAsync(TenantScope scope, CancellationToken ct)
    {
        var boundaryIds = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(scope, p => p.CurrentBoundaryId != null, cursor, Chunk, ct);
            boundaryIds.AddRange(page.Items.Select(p => p.CurrentBoundaryId!));
            cursor = page.NextCursor;
        } while (cursor is not null && boundaryIds.Count < MaxParcels);

        var result = new List<BoundaryVersion>();
        foreach (var ids in boundaryIds.Distinct().Chunk(Chunk))
        {
            var list = ids.ToList();
            result.AddRange(await store.Boundaries.FindAsync(scope, b => list.Contains(b.Id), Chunk, ct));
        }

        return result;
    }
}
