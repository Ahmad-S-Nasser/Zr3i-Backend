using Zr3i.Data;
using Zr3i.Kernel.Reports;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record ReportBody(string? Kind, string? SubjectId, string? Season);

/// <summary>
/// Frozen reports. Asking for one queues a job (nothing is rendered in the request); the worker freezes it and it then appears in the list.
/// An organisation lists and downloads only its own reports, and every download is audited.
/// </summary>
public static class ReportEndpoints
{
    public const string Read = "kernel:reports:read";
    public const string Write = "kernel:reports:write";

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/reports", async (string tenantId, HttpContext http, Guard guard, ReportService reports, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var l = await reports.ListAsync(TenantScope.ForTenant(tenantId), ct);
                return Results.Ok(new { ready = l.Ready.Select(Dto), pending = l.Pending.Select(p => new { reportId = p.ReportId, kind = p.Kind, season = p.Season, status = p.Status, errorCode = p.ErrorCode, requestedAt = p.RequestedAt }) });
            })).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/reports", async (string tenantId, ReportBody b, HttpContext http, Guard guard, ReportService reports, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var p = await reports.RequestAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.Kind, b.SubjectId, b.Season, ct);
                return Results.Accepted((string?)null, new { reportId = p.ReportId, kind = p.Kind, season = p.Season, status = p.Status });
            })).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/reports/{id}", async (string tenantId, string id, HttpContext http, Guard guard, ReportService reports, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () => Results.Ok(Dto(await reports.GetAsync(TenantScope.ForTenant(tenantId), id, ct))))).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/reports/{id}/files/{name}", async (string tenantId, string id, string name, HttpContext http, Guard guard, ReportService reports, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var f = await reports.DownloadAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), id, name, ct);
                http.Response.Headers["X-Content-SHA256"] = f.Sha256;
                return Results.File(f.Data, f.ContentType, f.Name);
            })).RequireAuthorization();
    }

    private static object Dto(FrozenReport r) => new
    {
        id = r.Id, kind = r.Kind, season = r.Season, subjectId = r.SubjectId, subjectName = r.SubjectName, createdAt = r.CreatedAt, createdBy = r.CreatedBy,
        snapshotSha256 = r.SnapshotSha256, manifestSha256 = r.ManifestSha256, methodVersions = r.MethodVersions, synthetic = r.Synthetic,
        files = r.Files.Select(f => new { name = f.Name, contentType = f.ContentType, bytes = f.Bytes, sha256 = f.Sha256 }),
    };
}
