using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record CriBody(
    double? NdmiMin, double? HeatStress, double? NdviIntegral, double? AreaVariancePct, double? NdviCohortPercentile,
    List<double>? ClosedSeasonYields, double? ValidFraction);

public sealed record AdjustmentBody(string? Key, double? Value);

public sealed record FriBody(List<AdjustmentBody>? Adjustments);

public sealed record ReferenceBody(string? Kind, string? CohortLevel, string? CohortKey, List<string>? Seasons, List<double>? Scores);

/// <summary>
/// Risk indicators (CRI per parcel-season, FRI per farm-season). Every result states its method and version and that it is specified and
/// not calibrated; the UI turns the stable codes into English or Arabic. Histories are in time order: nothing here sorts or ranks by value.
/// </summary>
public static class RiskEndpoints
{
    public const string Write = "risk:indicators:write";
    public const string Read = "risk:indicators:read";
    public const string WriteReferences = "risk:references:write";

    public static void MapRiskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organisations/{tenantId}/risk/parcel-seasons/{parcelSeasonId}/cri", async (string tenantId, string parcelSeasonId, CriBody b, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                if (b.ValidFraction is null) throw new ValidationFailedException("valid_fraction_required");
                var features = new CriFeatures(b.NdmiMin, b.HeatStress, b.NdviIntegral, b.AreaVariancePct, b.NdviCohortPercentile, b.ClosedSeasonYields, b.ValidFraction.Value);
                var result = await risk.RecordCriAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), parcelSeasonId, features, ct);
                return Results.Ok(Shape(new IndicatorView(result, true, null, null)));
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/risk/parcel-seasons/{parcelSeasonId}/cri", async (string tenantId, string parcelSeasonId, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
                Results.Ok((await risk.CriHistoryAsync(TenantScope.ForTenant(tenantId), parcelSeasonId, ct)).Select(Shape)))).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/risk/farms/{farmId}/seasons/{season}/fri", async (string tenantId, string farmId, string season, FriBody b, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var adjustments = (b.Adjustments ?? []).Select(a => new IndicatorAdjustment(a.Key ?? string.Empty, a.Value)).ToList();
                var outcome = await risk.RecordFriAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), farmId, season, adjustments, ct);
                return Results.Ok(new { result = Shape(new IndicatorView(outcome.Result, true, null, "farm_cohort_not_defined")), skipped = outcome.Skipped.Select(s => new { parcelSeasonId = s.ParcelSeasonId, reason = s.Reason }) });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/risk/farms/{farmId}/seasons/{season}/fri", async (string tenantId, string farmId, string season, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
                Results.Ok((await risk.FriHistoryAsync(TenantScope.ForTenant(tenantId), farmId, season, ct)).Select(Shape)))).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/risk/reference-distributions", async (string tenantId, ReferenceBody b, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, WriteReferences, ct, async () =>
            {
                var row = await risk.AddReferenceAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.Kind ?? string.Empty, b.CohortLevel ?? string.Empty, b.CohortKey ?? string.Empty, b.Seasons ?? [], b.Scores ?? [], ct);
                return Results.Ok(new { id = row.Id, kind = row.Kind, cohortLevel = row.CohortLevel, cohortKey = row.CohortKey, version = row.Version, size = row.Scores.Count, seasons = row.Seasons, scoresSha256 = row.ScoresSha256 });
            })).RequireAuthorization();
    }

    private static object Shape(IndicatorView v) => new
    {
        id = v.Result.Id, indicator = v.Result.Indicator, subjectType = v.Result.SubjectType, subjectId = v.Result.SubjectId,
        value = v.Result.Value, current = v.Current, supersedes = v.Result.Supersedes,
        maturity = v.Result.Maturity, label = "specified_not_calibrated",
        method = new { id = v.Result.MethodId, version = v.Result.MethodVersion },
        components = v.Result.Components.Select(c => new { key = c.Key, weight = c.Weight, score = c.Score, established = c.Established }),
        missingComponents = v.Result.MissingComponents,
        adjustments = v.Result.Adjustments.Select(a => new { key = a.Key, value = a.Value }),
        validFraction = v.Result.ValidFraction, insufficient = v.Result.Insufficient,
        inputSha256 = v.Result.InputSha256,
        band = v.Band is null ? null : new { band = v.Band.Band, percentile = v.Band.Percentile, cohortLevel = v.Band.CohortLevel, cohortKey = v.Band.CohortKey, referenceVersion = v.Band.ReferenceVersion, referenceSize = v.Band.ReferenceSize, referenceSha256 = v.Band.ReferenceSha256 },
        bandReason = v.BandReason, createdAt = v.Result.CreatedAt,
    };
}
