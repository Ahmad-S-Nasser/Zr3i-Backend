using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Imagery;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record DiscoverBody(DateTime? From, DateTime? To, double? MaxCloudCover);

public sealed record IngestBody(DateTime? From, DateTime? To, double? MaxCloudCover, string? Tile);

/// <summary>
/// Asking for satellite scenes over a customer's parcels. The request only queues searches (one per occupied map cell); the worker does
/// the fetching. The scene catalogue itself is public data and readable by anyone signed in.
/// </summary>
public static class ImageryEndpoints
{
    public const string Write = "kernel:imagery:write";
    public const string Read = "kernel:imagery:read";

    public static void MapImageryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organisations/{tenantId}/imagery/discover", async (string tenantId, DiscoverBody b, HttpContext http, Guard guard, DiscoveryPlanner planner, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                if (b.From is null || b.To is null) return Results.BadRequest(new { error = "invalid_period" });
                var jobs = await planner.PlanAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.From.Value, b.To.Value, b.MaxCloudCover ?? 100, ct);
                return Results.Accepted((string?)null, new { searches = jobs.Count, jobIds = jobs });
            })).RequireAuthorization();

        // Read the scenes in the period for this organisation's parcels. Also only queues work: the worker reads the imagery.
        app.MapPost("/api/organisations/{tenantId}/imagery/ingest", async (string tenantId, IngestBody b, HttpContext http, Guard guard, IngestPlanner planner, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                if (b.From is null || b.To is null) return Results.BadRequest(new { error = "invalid_period" });
                var jobs = await planner.PlanAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.From.Value, b.To.Value, b.MaxCloudCover ?? 100, b.Tile, ct);
                return Results.Accepted((string?)null, new { scenes = jobs.Count, jobIds = jobs });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/parcels/{parcelId}/observations", async (string tenantId, string parcelId, HttpContext http, Guard guard, KernelStore store, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                if (await store.Parcels.GetAsync(scope, parcelId, ct) is null) throw new NotFoundException("parcel", parcelId);
                var rows = (await store.ParcelObservations.FindAsync(scope, o => o.ParcelId == parcelId, 1000, ct)).OrderBy(o => o.SensingTime).ThenBy(o => o.SceneId, StringComparer.Ordinal);
                return Results.Ok(rows.Select(o => new
                {
                    sceneId = o.SceneId, sensingTime = o.SensingTime, boundaryId = o.BoundaryId, fullPixels = o.FullPixels, usablePixels = o.UsablePixels,
                    lostPixels = o.LostPixels, lostShare = o.LostShare, readable = o.Readable, reason = o.Reason, sclHistogram = o.SclHistogram,
                    ndvi = o.NdviMedian is null ? null : new { pixels = o.NdviPixels, excluded = o.NdviExcluded, median = o.NdviMedian, mean = o.NdviMean, min = o.NdviMin, max = o.NdviMax },
                    pixelStackSha256 = o.PixelStackSha256, sceneItemSha256 = o.SceneItemSha256, pixelMethodId = o.PixelMethodId,
                    method = new { id = o.MethodId, version = o.MethodVersion, state = o.MethodState }, reader = o.Reader, attribution = o.Attribution,
                }));
            })).RequireAuthorization();

        app.MapGet("/api/imagery/scenes", async (DateTime? from, DateTime? to, string? tile, SceneCatalog catalog, CancellationToken ct) =>
        {
            if (from is null || to is null || to <= from) return Results.BadRequest(new { error = "invalid_period" });
            var scenes = await catalog.ListAsync(from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), tile, ct);
            return Results.Ok(scenes.Select(s => new
            {
                sceneId = s.SceneId, sensingTime = s.SensingTime, tile = s.Tile, platform = s.Platform, cloudCover = s.CloudCover,
                processingBaseline = s.ProcessingBaseline, itemSha256 = s.ItemSha256, attribution = s.Attribution, source = s.Source,
            }));
        }).RequireAuthorization();
    }
}
