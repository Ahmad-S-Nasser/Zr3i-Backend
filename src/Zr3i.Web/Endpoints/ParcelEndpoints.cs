using Zr3i.Data;
using Zr3i.Kernel.Farms;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>The organisation's parcels, a page at a time, in a stable order that never depends on a finding or a risk value.</summary>
public static class ParcelEndpoints
{
    public const string Read = "portfolio:farms:read";

    public static void MapParcelEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/organisations/{tenantId}/parcels", async (string tenantId, string? cursor, int? pageSize, string? season, string? farmId, HttpContext http, Guard guard, ParcelListService parcels, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var list = await parcels.ListAsync(TenantScope.ForTenant(tenantId), cursor, pageSize, season, farmId, ct);
                return Results.Ok(new
                {
                    season = list.Season, nextCursor = list.NextCursor,
                    items = list.Items.Select(p => new
                    {
                        id = p.Id, label = p.Label, farmId = p.FarmId, farmName = p.FarmName, crop = p.Crop,
                        declaredAreaFeddan = p.DeclaredAreaFeddan, measuredAreaFeddan = p.MeasuredAreaFeddan,
                        finding = p.Finding is null ? null : new { status = p.Finding.Status, reason = p.Finding.Reason, gapPercent = p.Finding.GapPercent, noisePercent = p.Finding.NoisePercent, direction = p.Finding.Direction },
                    }),
                });
            })).RequireAuthorization();
}
