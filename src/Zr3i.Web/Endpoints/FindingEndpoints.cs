using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Findings;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record AssessBody(string? Season);

/// <summary>
/// Findings: what the platform states about a parcel in a season. Assessing only queues a job (the worker does the work). Reading
/// returns the facts, their status and where they came from; there is no approve, reject or recommend anywhere.
/// </summary>
public static class FindingEndpoints
{
    public const string Write = "verification:findings:write";
    public const string Read = "verification:findings:read";

    public static void MapFindingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organisations/{tenantId}/verification/assess", async (string tenantId, AssessBody b, HttpContext http, Guard guard, AssessmentPlanner planner, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var jobId = await planner.PlanAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.Season ?? string.Empty, ct);
                return Results.Accepted((string?)null, new { jobId });
            })).RequireAuthorization();

        // Every finding for a parcel, oldest first, so the history of a statement is visible; "current" marks the latest in each slot.
        app.MapGet("/api/organisations/{tenantId}/parcels/{parcelId}/findings", async (string tenantId, string parcelId, HttpContext http, Guard guard, KernelStore store, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                if (await store.Parcels.GetAsync(scope, parcelId, ct) is null) throw new NotFoundException("parcel", parcelId);
                var rows = (await store.Findings.FindAsync(scope, f => f.ParcelId == parcelId, 1000, ct)).OrderBy(f => f.CreatedAt).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
                var superseded = rows.Where(f => f.Supersedes != null).Select(f => f.Supersedes!).ToHashSet();
                return Results.Ok(rows.Select(f => Shape(f, !superseded.Contains(f.Id))));
            })).RequireAuthorization();
    }

    public static void MapAccuracyEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/organisations/{tenantId}/verification/accuracy", async (string tenantId, HttpContext http, Guard guard, AccuracyRegister register, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () => Results.Ok(await register.ListAsync(TenantScope.ForTenant(tenantId), ct)))).RequireAuthorization();

    private static object Shape(Finding f, bool current) => new
    {
        id = f.Id, parcelSeasonId = f.ParcelSeasonId, season = f.Season, type = f.Type, status = f.Status, reason = f.Reason, current, supersedes = f.Supersedes,
        declared = new { crop = f.DeclaredCrop, areaFeddan = f.DeclaredAreaFeddan, sowingDate = f.DeclaredSowingDate, source = f.DeclaredSource },
        area = f.Type == "area_gap" && f.MeasuredFeddan is not null
            ? new { measuredFeddan = f.MeasuredFeddan, gapFeddan = f.GapFeddan, gapPercent = f.GapPercent, noisePercent = f.NoisePercent, direction = f.Direction }
            : null,
        crop = f.Slot == FindingSlots.Crop
            ? new { scenesUsed = f.ScenesUsed, passesUsed = f.PassesUsed, maxNdvi = f.MaxNdvi, rulesTested = f.RulesTested, observationIds = f.ObservationIds }
            : null,
        method = new { id = f.MethodId, version = f.MethodVersion, state = f.MethodState, parametersSha256 = f.ParametersSha256 },
        cropRule = f.CropRuleSource is null ? null : new { source = f.CropRuleSource, configId = f.CropConfigId, configVersion = f.CropConfigVersion, sha256 = f.CropRuleSha256 },
        boundary = new { id = f.BoundaryId, geometrySha256 = f.BoundaryGeometrySha256 },
        accuracyRef = f.AccuracyRef, createdAt = f.CreatedAt,
    };
}
