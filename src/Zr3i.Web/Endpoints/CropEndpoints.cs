using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Crops;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record CropVersionBody(int? EmergenceToleranceDays, int? PassMinSeparationDays, string? SowingWindow, int? CycleDays, Dictionary<string, double>? NdviThresholds, string? Reason);

public sealed record ActivateBody(string? ApprovedBy);

/// <summary>
/// Per-crop settings. Everyone signed in can read what is configured; only Zr3i administrators can add a version or activate one, and
/// activating needs a named approver.
/// </summary>
public static class CropEndpoints
{
    public const string Manage = "platform:crops:manage";

    public static void MapCropEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/crops", async (CropConfigs crops, CancellationToken ct) =>
            Results.Ok((await crops.CropsAsync(ct)).Select(c => new { code = c.CropCode, activeVersion = c.ActiveVersion }))).RequireAuthorization();

        app.MapGet("/api/crops/{code}/versions", async (string code, CropConfigs crops, CancellationToken ct) =>
        {
            var versions = await crops.VersionsAsync(code, ct);
            if (versions.Count == 0) return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
            var active = (await crops.ActiveAsync(code, ct))?.Version;
            return Results.Ok(versions.Select(v => Shape(v, v.Version == active)));
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/platform/crops/{code}").RequireAuthorization();

        admin.MapPost("versions", async (string code, CropVersionBody b, HttpContext http, Guard guard, CropConfigs crops, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                var settings = new CropSettings
                {
                    EmergenceToleranceDays = b.EmergenceToleranceDays, PassMinSeparationDays = b.PassMinSeparationDays, SowingWindow = b.SowingWindow,
                    CycleDays = b.CycleDays, NdviThresholds = new SortedDictionary<string, double>(b.NdviThresholds ?? []),
                };
                var v = await crops.CreateVersionAsync(http.User.UserId(), code, settings, b.Reason ?? string.Empty, ct);
                return Results.Created((string?)null, Shape(v, false));
            }));

        admin.MapPost("versions/{version:int}/activate", async (string code, int version, ActivateBody b, HttpContext http, Guard guard, CropConfigs crops, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                var a = await crops.ActivateAsync(http.User.UserId(), code, version, b.ApprovedBy ?? string.Empty, ct);
                return Results.Ok(new { crop = a.CropCode, version = a.Version, approvedBy = a.ApprovedBy, activatedAt = a.ActivatedAt });
            }));
    }

    private static object Shape(CropConfigVersion v, bool active) => new
    {
        crop = v.CropCode, version = v.Version, active, reason = v.Reason, createdAt = v.CreatedAt, createdBy = v.CreatedBy, sha256 = v.Sha256,
        settings = new
        {
            emergenceToleranceDays = v.Settings.EmergenceToleranceDays, passMinSeparationDays = v.Settings.PassMinSeparationDays,
            sowingWindow = v.Settings.SowingWindow, cycleDays = v.Settings.CycleDays, ndviThresholds = v.Settings.NdviThresholds,
        },
    };
}
