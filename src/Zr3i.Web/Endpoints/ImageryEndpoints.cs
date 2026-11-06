using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Chips;
using Zr3i.Kernel.Imagery;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record DiscoverBody(DateTime? From, DateTime? To, double? MaxCloudCover);

public sealed record ChipBody(string? SceneId);

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

        // What the Satellite screen shows: whether searches and readings are running, and the scenes that cover this organisation's parcels.
        app.MapGet("/api/organisations/{tenantId}/imagery/status", async (string tenantId, HttpContext http, Guard guard, ImagerySummaryService summary, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var s = await summary.StatusAsync(TenantScope.ForTenant(tenantId), ct);
                static object Counts(JobCounts c) => new { queued = c.Queued, running = c.Running, succeeded = c.Succeeded, failed = c.Failed, errorCodes = c.ErrorCodes };
                return Results.Ok(new { discover = Counts(s.Discover), ingest = Counts(s.Ingest), chip = Counts(s.Chip) });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/imagery/scenes", async (string tenantId, DateTime? from, DateTime? to, HttpContext http, Guard guard, ImagerySummaryService summary, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                if (from is null || to is null) return Results.BadRequest(new { error = "invalid_period" });
                var o = await summary.ScenesAsync(TenantScope.ForTenant(tenantId), from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), ct);
                return Results.Ok(new
                {
                    hasParcels = o.HasParcels, total = o.Total,
                    scenes = o.Scenes.Select(r => new
                    {
                        sceneId = r.Scene.SceneId, sensingTime = r.Scene.SensingTime, tile = r.Scene.Tile, platform = r.Scene.Platform, cloudCover = r.Scene.CloudCover,
                        attribution = r.Scene.Attribution, parcelsUnder = r.ParcelsUnder, observed = r.Observed, readable = r.Readable,
                    }),
                });
            })).RequireAuthorization();

        // Pictures cut from a scene around a parcel. Asking only queues the work; the worker reads the bands and draws.
        app.MapPost("/api/organisations/{tenantId}/parcels/{parcelId}/chips", async (string tenantId, string parcelId, ChipBody b, HttpContext http, Guard guard, ChipPlanner planner, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var r = await planner.PlanAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), parcelId, b.SceneId, ct);
                return r.AlreadyMade ? Results.Ok(new { status = "ready" }) : Results.Accepted((string?)null, new { status = "queued", jobId = r.JobId });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/parcels/{parcelId}/chips", async (string tenantId, string parcelId, HttpContext http, Guard guard, ChipService chips, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
                Results.Ok((await chips.ListAsync(TenantScope.ForTenant(tenantId), parcelId, ct)).Select(c => new
                {
                    id = c.Id, sceneId = c.SceneId, kind = c.Kind, sensingTime = c.SensingTime, widthPx = c.WidthPx, heightPx = c.HeightPx, scale = c.Scale, sha256 = c.Sha256,
                    sceneItemSha256 = c.SceneItemSha256, pixelStackSha256 = c.PixelStackSha256, pixelMethodId = c.PixelMethodId, reader = c.Reader, attribution = c.Attribution, createdAt = c.CreatedAt,
                })))).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/parcels/{parcelId}/chips/{chipId}/image", async (string tenantId, string parcelId, string chipId, HttpContext http, Guard guard, ChipService chips, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var (chip, data) = await chips.ReadAsync(TenantScope.ForTenant(tenantId), parcelId, chipId, ct);
                http.Response.Headers["X-Content-SHA256"] = chip.Sha256;
                http.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(data, "image/png");
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
