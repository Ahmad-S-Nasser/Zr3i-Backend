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

public static class ParcelSceneStatus
{
    public const string Usable = "usable";
    public const string SceneLost = "scene_lost";
    public const string BelowResolution = "below_resolution";

    /// <summary>The parcel lies under the scene but has no reading of it yet.</summary>
    public const string NotRead = "not_read";
}

/// <summary>One parcel under one scene: what the reading found (if there is one) and which pictures exist.</summary>
public sealed record ParcelSceneRow(
    string ParcelId, string Label, string FarmName, string Status, int? FullPixels, int? UsablePixels, double? LostShare, double? NdviMedian,
    IReadOnlyList<ChipRef> Chips);

public sealed record ChipRef(string Id, string Kind);

public sealed record SceneDetail(Scene Scene, IReadOnlyList<ParcelSceneRow> Parcels, int Total);

/// <summary>
/// What the Satellite screen shows: whether searches and readings are running, and which catalogue scenes cover this organisation's parcels.
/// Scenes are public facts; the counts come only from this organisation's own parcels and observations.
/// </summary>
public sealed class ImagerySummaryService(KernelStore store, JobQueue jobs, SceneCatalog catalog)
{
    public const int MaxScenes = 100;
    private const int Chunk = 500;
    private const int MaxParcels = 20000;
    public const int MaxSceneParcels = 500;

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

    /// <summary>
    /// One scene, and every parcel of the organisation that lies under it with what its reading found. Listed by label, never by value (no ranking).
    /// A parcel is shown against its current boundary only; a reading made against an older boundary does not count.
    /// </summary>
    /// <exception cref="NotFoundException">scene.</exception>
    public async Task<SceneDetail> SceneDetailAsync(TenantScope scope, string sceneId, CancellationToken ct = default)
    {
        var scene = (await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SceneId == sceneId, 1, ct)).FirstOrDefault() ?? throw new NotFoundException("scene", sceneId);

        var parcels = new List<Parcel>();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(scope, p => p.CurrentBoundaryId != null, cursor, Chunk, ct);
            parcels.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null && parcels.Count < MaxParcels);

        var boundaries = new Dictionary<string, BoundaryVersion>();
        foreach (var ids in parcels.Select(p => p.CurrentBoundaryId!).Distinct().Chunk(Chunk))
        {
            var list = ids.ToList();
            foreach (var b in await store.Boundaries.FindAsync(scope, b => list.Contains(b.Id), Chunk, ct)) boundaries[b.Id] = b;
        }

        var under = parcels.Where(p => boundaries.TryGetValue(p.CurrentBoundaryId!, out var b)
            && b.MinLon <= scene.MaxLon && b.MaxLon >= scene.MinLon && b.MinLat <= scene.MaxLat && b.MaxLat >= scene.MinLat).OrderBy(p => p.Label, StringComparer.Ordinal).ToList();

        var observations = (await store.ParcelObservations.FindAsync(scope, o => o.SceneId == sceneId, 1000, ct)).ToList();
        var chips = (await store.ParcelChips.FindAsync(scope, c => c.SceneId == sceneId, 1000, ct)).ToList();
        var farmIds = under.Select(p => p.FarmId).Distinct().ToList();
        var farmNames = new Dictionary<string, string>();
        foreach (var ids in farmIds.Chunk(Chunk))
        {
            var list = ids.ToList();
            foreach (var f in await store.Farms.FindAsync(scope, f => list.Contains(f.Id), Chunk, ct)) farmNames[f.Id] = f.Name;
        }

        var rows = under.Take(MaxSceneParcels).Select(p =>
        {
            var o = observations.FirstOrDefault(x => x.ParcelId == p.Id && x.BoundaryId == p.CurrentBoundaryId);
            var status = o is null ? ParcelSceneStatus.NotRead : o.Reason == ObservationReasons.BelowResolution ? ParcelSceneStatus.BelowResolution : o.Readable ? ParcelSceneStatus.Usable : ParcelSceneStatus.SceneLost;
            var made = chips.Where(c => c.ParcelId == p.Id && c.BoundaryId == p.CurrentBoundaryId).OrderBy(c => c.Kind, StringComparer.Ordinal).Select(c => new ChipRef(c.Id, c.Kind)).ToList();
            return new ParcelSceneRow(p.Id, p.Label, farmNames.GetValueOrDefault(p.FarmId) ?? string.Empty, status, o?.FullPixels, o?.UsablePixels, o?.LostShare, o?.NdviMedian, made);
        }).ToList();

        return new SceneDetail(scene, rows, under.Count);
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
