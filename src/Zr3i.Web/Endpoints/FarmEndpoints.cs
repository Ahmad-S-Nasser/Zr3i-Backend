using Zr3i.Data;
using Zr3i.Kernel.Farms;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>The organisation's farms, a page at a time, in a stable order that never depends on risk.</summary>
public static class FarmEndpoints
{
    public const string Read = "portfolio:farms:read";

    public static void MapFarmEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/organisations/{tenantId}/farms", async (string tenantId, string? cursor, int? pageSize, string? season, HttpContext http, Guard guard, FarmListService farms, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var list = await farms.ListAsync(TenantScope.ForTenant(tenantId), cursor, pageSize, season, ct);
                return Results.Ok(new
                {
                    season = list.Season, nextCursor = list.NextCursor,
                    items = list.Items.Select(f => new
                    {
                        id = f.Id, name = f.Name, governorate = f.Governorate, markaz = f.Markaz, parcelCount = f.ParcelCount, areaFeddan = f.AreaFeddan,
                        fri = f.Fri is null ? null : new { value = f.Fri.Value, createdAt = f.Fri.CreatedAt, maturity = f.Fri.Maturity, label = "specified_not_calibrated", method = new { id = f.Fri.MethodId, version = f.Fri.MethodVersion } },
                    }),
                });
            })).RequireAuthorization();
}
