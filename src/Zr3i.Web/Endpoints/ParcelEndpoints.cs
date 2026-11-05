using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>
/// The organisation's parcels, a page at a time, in a stable order that never depends on a finding or a risk value; one parcel; and the
/// counts behind the Data analysis screen.
/// </summary>
public static class ParcelEndpoints
{
    public const string Read = "portfolio:farms:read";
    public const string ReadAnalysis = "verification:findings:read";

    public static void MapParcelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/parcels", async (string tenantId, string? cursor, int? pageSize, string? season, string? farmId, string? verdict, string? q, HttpContext http, Guard guard, ParcelListService parcels, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var list = await parcels.ListAsync(TenantScope.ForTenant(tenantId), cursor, pageSize, season, farmId, ct, verdict, q);
                return Results.Ok(new { season = list.Season, nextCursor = list.NextCursor, items = list.Items.Select(Shape) });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/parcels/{parcelId}", async (string tenantId, string parcelId, string? season, HttpContext http, Guard guard, ParcelListService parcels, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
                Results.Ok(Shape(await parcels.GetAsync(TenantScope.ForTenant(tenantId), parcelId, season, ct) ?? throw new NotFoundException("parcel", parcelId))))).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/verification/analysis", async (string tenantId, string? season, HttpContext http, Guard guard, VerificationAnalysisService analysis, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, ReadAnalysis, ct, async () =>
            {
                var a = await analysis.SummaryAsync(TenantScope.ForTenant(tenantId), season ?? string.Empty, ct);
                return Results.Ok(new { season = a.Season, parcels = a.Parcels, verdicts = a.Verdicts, scenesPerMonth = a.ScenesPerMonth.Select(m => new { month = m.Month, scenes = m.Scenes }), accuracy = a.Accuracy });
            })).RequireAuthorization();
    }

    private static object Shape(ParcelListItem p) => new
    {
        id = p.Id, label = p.Label, farmId = p.FarmId, farmName = p.FarmName, crop = p.Crop,
        declaredAreaFeddan = p.DeclaredAreaFeddan, measuredAreaFeddan = p.MeasuredAreaFeddan,
        finding = p.Finding is null ? null : new { status = p.Finding.Status, reason = p.Finding.Reason, gapPercent = p.Finding.GapPercent, noisePercent = p.Finding.NoisePercent, direction = p.Finding.Direction },
    };
}
