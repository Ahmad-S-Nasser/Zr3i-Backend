using Zr3i.Data;
using Zr3i.Modules.Verification;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>The size classes in force. Null until the edges are configured: they come from the R0 specification and are never guessed.</summary>
public sealed record SizeClassConfig(SizeClassScheme? Scheme);

public static class ReadabilityEndpoints
{
    public const string Read = "kernel:readability:read";

    public static void MapReadabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/readability", async (
            string tenantId, HttpContext http, Guard guard, ReadabilityService service, SizeClassConfig sizeClasses, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                if (sizeClasses.Scheme is null) return Results.Json(new { error = "size_classes_not_configured" }, statusCode: StatusCodes.Status409Conflict);

                var report = await service.ReportAsync(TenantScope.ForTenant(tenantId), sizeClasses.Scheme, ct);
                return Results.Ok(new
                {
                    methodId = report.MethodId,
                    bufferMetres = report.BufferMetres,
                    minFullPixels = report.MinFullPixels,
                    classes = report.Classes.Select(c => new { key = c.Key, fromFeddan = c.FromFeddan, toFeddan = c.ToFeddan, parcels = c.Parcels, readable = c.Readable, share = c.Share }),
                    parcelsWithoutBoundary = report.ParcelsWithoutBoundary,
                });
            })).RequireAuthorization();
    }
}
