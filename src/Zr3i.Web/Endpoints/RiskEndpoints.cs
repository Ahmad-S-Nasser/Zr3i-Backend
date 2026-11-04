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

        app.MapGet("/api/organisations/{tenantId}/risk/parcel-seasons/{parcelSeasonId}/summary", async (string tenantId, string parcelSeasonId, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var s = await risk.ParcelSummaryAsync(TenantScope.ForTenant(tenantId), parcelSeasonId, ct);
                return Results.Ok(new
                {
                    maturity = IndicatorMaturity.Specified, label = "specified_not_calibrated", method = new { id = CriCalculator.MethodId, version = CriCalculator.MethodVersion },
                    subject = new { parcelSeasonId = s.Subject.ParcelSeasonId, parcelId = s.Subject.ParcelId, label = s.Subject.Label, farmId = s.Subject.FarmId, farmName = s.Subject.FarmName, season = s.Subject.Season, crop = s.Subject.Crop, areaFeddan = s.Subject.AreaFeddan },
                    current = s.Current is null ? null : Shape(s.Current), history = s.History.Select(Shape),
                });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/risk/farms/{farmId}/seasons/{season}/summary", async (string tenantId, string farmId, string season, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var s = await risk.FarmSummaryAsync(TenantScope.ForTenant(tenantId), farmId, season, ct);
                return Results.Ok(new
                {
                    maturity = IndicatorMaturity.Specified, label = "specified_not_calibrated", method = new { id = FriCalculator.MethodId, version = FriCalculator.MethodVersion },
                    farm = new { id = s.Farm.Id, name = s.Farm.Name, governorate = s.Farm.Governorate }, season = s.Season,
                    fri = s.Fri is null ? null : Shape(s.Fri), friStale = s.FriStale, awcri = s.Awcri, adjustmentsSum = s.AdjustmentsSum,
                    parcelCount = s.ParcelCount, totalAreaFeddan = s.TotalAreaFeddan, seasonsOnFile = s.SeasonsOnFile,
                    parcels = s.Parcels.Select(p => new
                    {
                        parcelSeasonId = p.ParcelSeasonId, parcelId = p.ParcelId, label = p.Label, crop = p.Crop, areaFeddan = p.AreaFeddan, cri = p.Cri, areaTimesCri = p.AreaTimesCri,
                        status = p.Status, closedSeasons = p.ClosedSeasons, band = BandShape(p.Band), bandReason = p.BandReason,
                    }),
                    exposure = s.Exposure is null ? null : new { parcelSeasonId = s.Exposure.ParcelSeasonId, label = s.Exposure.Label, crop = s.Exposure.Crop, areaFeddan = s.Exposure.AreaFeddan, cri = s.Exposure.Cri, areaSharePercent = s.Exposure.AreaSharePercent, awcriWithout = s.Exposure.AwcriWithout },
                });
            })).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/risk/reference-distributions", async (string tenantId, ReferenceBody b, HttpContext http, Guard guard, RiskService risk, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, WriteReferences, ct, async () =>
            {
                var row = await risk.AddReferenceAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.Kind ?? string.Empty, b.CohortLevel ?? string.Empty, b.CohortKey ?? string.Empty, b.Seasons ?? [], b.Scores ?? [], ct);
                return Results.Ok(new { id = row.Id, kind = row.Kind, cohortLevel = row.CohortLevel, cohortKey = row.CohortKey, version = row.Version, size = row.Scores.Count, seasons = row.Seasons, scoresSha256 = row.ScoresSha256 });
            })).RequireAuthorization();
    }

    private static double EstablishedWeight(IndicatorResult r) => r.Components.Where(c => c.Established).Sum(c => c.Weight);

    private static object? BandShape(BandAssessment? b) => b is null ? null
        : new { band = b.Band, percentile = b.Percentile, cohortLevel = b.CohortLevel, cohortKey = b.CohortKey, referenceVersion = b.ReferenceVersion, referenceSize = b.ReferenceSize, referenceSha256 = b.ReferenceSha256 };

    private static object Shape(IndicatorView v) => new
    {
        id = v.Result.Id, indicator = v.Result.Indicator, subjectType = v.Result.SubjectType, subjectId = v.Result.SubjectId,
        value = v.Result.Value, current = v.Current, supersedes = v.Result.Supersedes,
        maturity = v.Result.Maturity, label = "specified_not_calibrated",
        method = new { id = v.Result.MethodId, version = v.Result.MethodVersion },
        components = v.Result.Components.Select(c => new
        {
            key = c.Key, weight = c.Weight, score = c.Score, established = c.Established, feature = c.Feature, featureValue = c.FeatureValue,
            contribution = c.Score is { } sc && EstablishedWeight(v.Result) > 0 ? sc * c.Weight / EstablishedWeight(v.Result) : (double?)null,
        }),
        establishedWeight = EstablishedWeight(v.Result), weightedSum = v.Result.Components.Where(c => c.Established).Sum(c => c.Score!.Value * c.Weight), closedSeasons = v.Result.ClosedSeasons,
        missingComponents = v.Result.MissingComponents,
        adjustments = v.Result.Adjustments.Select(a => new { key = a.Key, value = a.Value }),
        validFraction = v.Result.ValidFraction, insufficient = v.Result.Insufficient,
        inputSha256 = v.Result.InputSha256,
        band = BandShape(v.Band),
        bandReason = v.BandReason, createdAt = v.Result.CreatedAt,
    };
}
