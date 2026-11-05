using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Programmes;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record TrancheBody(string? Name, string? Season, string? ScheduledDate, string[]? FarmIds, string[]? ParcelIds, string[]? ParcelExternalIds);

public sealed record EventWindowBody(string? Name, string? Season, string? From, string? To, string[]? FarmIds, string[]? ParcelIds, string[]? ParcelExternalIds);

/// <summary>
/// Tranches and event windows. Both group the organisation's own parcels and read only its own data; nothing here crosses to another organisation.
/// Checks and evidence state counts and numbers and never a decision.
/// </summary>
public static class TrancheEventEndpoints
{
    public const string TranchesRead = "kernel:tranches:read";
    public const string TranchesWrite = "kernel:tranches:write";
    public const string EventsRead = "kernel:events:read";
    public const string EventsWrite = "kernel:events:write";

    public static void MapTrancheEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/tranches", async (string tenantId, HttpContext http, Guard guard, TrancheService tranches, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, TranchesRead, ct, async () => Results.Ok((await tranches.ListAsync(TenantScope.ForTenant(tenantId), ct)).Select(TrancheDto)))).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/tranches", async (string tenantId, TrancheBody b, HttpContext http, Guard guard, TrancheService tranches, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, TranchesWrite, ct, async () =>
            {
                var t = await tranches.CreateAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), new TrancheRequest(b.Name, b.Season, b.ScheduledDate, new ParcelSelection(b.FarmIds, b.ParcelIds, b.ParcelExternalIds)), ct);
                return Results.Created((string?)null, TrancheDto(t));
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/tranches/{id}/check", async (string tenantId, string id, string? season, HttpContext http, Guard guard, TrancheService tranches, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, TranchesRead, ct, async () =>
            {
                var c = await tranches.CheckAsync(TenantScope.ForTenant(tenantId), id, season, ct);
                return Results.Ok(new
                {
                    tranche = TrancheDto(c.Tranche), verdicts = c.Verdicts, accuracy = c.Accuracy, needsALookTotal = c.NeedsALookTotal,
                    needsALook = c.NeedsALook.Select(p => new
                    {
                        id = p.Id, label = p.Label, farmName = p.FarmName, crop = p.Crop, declaredAreaFeddan = p.DeclaredAreaFeddan, measuredAreaFeddan = p.MeasuredAreaFeddan,
                        finding = p.Finding is null ? null : new { status = p.Finding.Status, reason = p.Finding.Reason, gapPercent = p.Finding.GapPercent, noisePercent = p.Finding.NoisePercent, direction = p.Finding.Direction },
                    }),
                });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/event-windows", async (string tenantId, HttpContext http, Guard guard, EventWindowService windows, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, EventsRead, ct, async () => Results.Ok((await windows.ListAsync(TenantScope.ForTenant(tenantId), ct)).Select(WindowDto)))).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/event-windows", async (string tenantId, EventWindowBody b, HttpContext http, Guard guard, EventWindowService windows, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, EventsWrite, ct, async () =>
            {
                var w = await windows.CreateAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), new EventWindowRequest(b.Name, b.Season, b.From, b.To, new ParcelSelection(b.FarmIds, b.ParcelIds, b.ParcelExternalIds)), ct);
                return Results.Created((string?)null, WindowDto(w));
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/event-windows/{id}/evidence", async (string tenantId, string id, HttpContext http, Guard guard, EventWindowService windows, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, EventsRead, ct, async () =>
            {
                var e = await windows.EvidenceAsync(TenantScope.ForTenant(tenantId), id, ct);
                static object? Scene(SceneRef? s) => s is null ? null : new { sceneId = s.SceneId, sensingTime = s.SensingTime, ndvi = s.Ndvi };
                return Results.Ok(new
                {
                    window = WindowDto(e.Window), coveredParcels = e.CoveredParcels, coveredFarms = e.CoveredFarms, parcelsWithEvidence = e.ParcelsWithEvidence,
                    parcels = e.Parcels.Select(p => new { id = p.ParcelId, label = p.Label, farmName = p.FarmName, before = Scene(p.Before), after = Scene(p.After), ndviChange = p.NdviChange, status = p.Status, reason = p.Reason }),
                });
            })).RequireAuthorization();
    }

    private static object TrancheDto(Tranche t) => new { id = t.Id, name = t.Name, season = t.Season, scheduledDate = t.ScheduledDate, parcelCount = t.ParcelIds.Count, createdAt = t.CreatedAt };

    private static object WindowDto(EventWindow w) => new { id = w.Id, name = w.Name, season = w.Season, from = w.From, to = w.To, parcelCount = w.ParcelIds.Count, createdAt = w.CreatedAt };
}
