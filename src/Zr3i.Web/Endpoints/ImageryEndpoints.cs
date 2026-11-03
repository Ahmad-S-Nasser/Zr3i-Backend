using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Imagery;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record DiscoverBody(DateTime? From, DateTime? To, double? MaxCloudCover);

/// <summary>
/// Asking for satellite scenes over a customer's parcels. The request only queues searches (one per occupied map cell); the worker does
/// the fetching. The scene catalogue itself is public data and readable by anyone signed in.
/// </summary>
public static class ImageryEndpoints
{
    public const string Write = "kernel:imagery:write";

    public static void MapImageryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organisations/{tenantId}/imagery/discover", async (string tenantId, DiscoverBody b, HttpContext http, Guard guard, DiscoveryPlanner planner, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                if (b.From is null || b.To is null) return Results.BadRequest(new { error = "invalid_period" });
                var jobs = await planner.PlanAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.From.Value, b.To.Value, b.MaxCloudCover ?? 100, ct);
                return Results.Accepted((string?)null, new { searches = jobs.Count, jobIds = jobs });
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
