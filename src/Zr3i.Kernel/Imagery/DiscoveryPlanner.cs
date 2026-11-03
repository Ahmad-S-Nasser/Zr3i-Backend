using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Imagery;

/// <summary>What the discover job is asked to do: find scenes over one box, in one period.</summary>
public sealed record DiscoverPayload(double MinLon, double MinLat, double MaxLon, double MaxLat, DateTime From, DateTime To, double MaxCloudCover);

/// <summary>
/// Turns a tenant's parcels into scene searches. One search per occupied cell of the map (a quarter of a degree, about 25 km) rather than
/// one huge box over a spread-out book, so a customer with farms in the Delta and in Upper Egypt does not ask for the desert between.
/// The searches are queued as jobs; nothing is fetched in the request.
/// </summary>
public sealed class DiscoveryPlanner(KernelStore store, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const double CellDegrees = 0.25;
    public const int MaxCells = 200;
    public const string JobType = "imagery.discover";

    /// <exception cref="ValidationFailedException">invalid_period, nothing_to_search, too_many_cells.</exception>
    public async Task<IReadOnlyList<string>> PlanAsync(TenantScope scope, string actorUserId, DateTime from, DateTime to, double maxCloudCover = 100, CancellationToken ct = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(400)) throw new ValidationFailedException("invalid_period");
        if (!(maxCloudCover is >= 0 and <= 100)) throw new ValidationFailedException("invalid_cloud_limit");

        var cells = await OccupiedCellsAsync(scope, ct);
        if (cells.Count == 0) throw new ValidationFailedException("nothing_to_search");
        if (cells.Count > MaxCells) throw new ValidationFailedException("too_many_cells");

        var now = clock.GetUtcNow().UtcDateTime;
        var ids = new List<string>();
        foreach (var (cx, cy) in cells.OrderBy(c => c.X).ThenBy(c => c.Y))
        {
            var payload = new DiscoverPayload(cx * CellDegrees, cy * CellDegrees, (cx + 1) * CellDegrees, (cy + 1) * CellDegrees, from, to, maxCloudCover);
            ids.Add((await jobs.EnqueueAsync(scope, JobType, JobJson.Serialize(payload), actorUserId, now, ct: ct)).Id);
        }

        await audit.RecordAsync(scope, actorUserId, "imagery.discovery_requested", "imagery", string.Join(',', ids.Take(3)), purpose: $"{cells.Count} searches", ct: ct);
        return ids;
    }

    /// <summary>The map cells that hold at least one parcel's current boundary, by the boundary's centre.</summary>
    public async Task<IReadOnlyList<(int X, int Y)>> OccupiedCellsAsync(TenantScope scope, CancellationToken ct = default)
    {
        var cells = new HashSet<(int, int)>();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(scope, p => p.CurrentBoundaryId != null, cursor, 500, ct);
            var ids = page.Items.Select(p => p.CurrentBoundaryId!).ToList();
            if (ids.Count > 0)
                foreach (var b in await store.Boundaries.FindAsync(scope, b => ids.Contains(b.Id), 500, ct))
                    cells.Add(((int)Math.Floor(b.CentroidLon / CellDegrees), (int)Math.Floor(b.CentroidLat / CellDegrees)));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return [.. cells];
    }
}
