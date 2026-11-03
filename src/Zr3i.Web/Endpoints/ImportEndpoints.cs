using System.Text;
using Microsoft.AspNetCore.Mvc;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Imports;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

/// <summary>
/// Bulk import. The upload returns at once (202) and the worker does the rest; the client polls the batch until it is
/// "validated", shows the error report, then asks for the commit and polls until "committed".
/// </summary>
public static class ImportEndpoints
{
    public const string Read = "kernel:imports:read";
    public const string Write = "kernel:imports:write";

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/organisations/{tenantId}/imports").RequireAuthorization();

        imports.MapPost("", async (string tenantId, string? fileName, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var batch = await service.UploadAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), fileName, http.Request.Body, ct);
                return Results.Accepted($"/api/organisations/{tenantId}/imports/{batch.Id}", Dto(new ImportView(batch, 0, 0, "queued")));
            }))
            .WithMetadata(new RequestSizeLimitAttribute(ImportService.MaxBytes + 4096));

        imports.MapGet("", async (string tenantId, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
                Results.Ok((await service.ListAsync(TenantScope.ForTenant(tenantId), ct: ct)).Select(b => Dto(new ImportView(b, 0, 0, null))))));

        imports.MapGet("{id}", async (string tenantId, string id, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () => Results.Ok(Dto(await service.GetAsync(TenantScope.ForTenant(tenantId), id, ct)))));

        imports.MapGet("{id}/errors", async (string tenantId, string id, string? cursor, int? pageSize, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                var batch = (await service.GetAsync(scope, id, ct)).Batch;
                var page = await service.ErrorsPageAsync(scope, id, cursor, Math.Clamp(pageSize ?? 100, 1, 500), ct);
                string Cell(ImportRow r, string column) { var at = batch.Header.IndexOf(column); return at >= 0 && at < r.Values.Count ? r.Values[at] : string.Empty; }
                return Results.Ok(new
                {
                    fileErrors = batch.FileErrors,
                    rows = page.Items.Select(r => new
                    {
                        line = r.RowNumber,
                        parcelExternalId = Cell(r, ImportColumns.ParcelExternalId),
                        farmExternalId = Cell(r, ImportColumns.FarmExternalId),
                        errors = r.Errors.Select(e => new { field = e.Field, code = e.Code }),
                    }),
                    nextCursor = page.NextCursor,
                });
            }));

        imports.MapGet("{id}/errors.csv", async (string tenantId, string id, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Read, ct, async () =>
            {
                var csv = await service.ErrorReportCsvAsync(TenantScope.ForTenant(tenantId), id, ct);
                // A byte-order mark so Excel opens Arabic text correctly.
                return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"import-{id}-errors.csv");
            }));

        imports.MapPost("{id}/commit", async (string tenantId, string id, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                var batch = await service.CommitAsync(scope, http.User.UserId(), id, ct);
                return Results.Accepted($"/api/organisations/{tenantId}/imports/{batch.Id}", Dto(await service.GetAsync(scope, batch.Id, ct)));
            }));

        imports.MapPost("{id}/discard", async (string tenantId, string id, HttpContext http, Guard guard, ImportService service, CancellationToken ct) =>
            await guard.RunAsync(http, tenantId, Write, ct, async () =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                await service.DiscardAsync(scope, http.User.UserId(), id, ct);
                return Results.Ok(Dto(await service.GetAsync(scope, id, ct)));
            }));
    }

    private static object Dto(ImportView v) => new
    {
        id = v.Batch.Id,
        status = v.Batch.Status,
        fileName = v.Batch.FileName,
        sizeBytes = v.Batch.SizeBytes,
        fileSha256 = v.Batch.FileSha256,
        totalRows = v.Batch.TotalRows,
        validRows = v.Batch.ValidRows,
        invalidRows = v.Batch.InvalidRows,
        created = v.Batch.Created,
        updated = v.Batch.Updated,
        unchanged = v.Batch.Unchanged,
        failed = v.Batch.Failed,
        fileErrors = v.Batch.FileErrors,
        fileWarnings = v.Batch.FileWarnings,
        createdAt = v.Batch.CreatedAt,
        validatedAt = v.Batch.ValidatedAt,
        committedAt = v.Batch.CommittedAt,
        progress = new { done = v.ProgressDone, total = v.ProgressTotal },
        jobStatus = v.JobStatus,
    };
}

/// <summary>Runs a route body after the permission check and turns the Kernel's exceptions into stable error responses.</summary>
public sealed class Guard(AuthorizationService authorization, AuditService audit)
{
    public async Task<IResult> RunAsync(HttpContext http, string tenantId, string permission, CancellationToken ct, Func<Task<IResult>> body)
    {
        if (await TenantAccess.DenyUnlessAsync(http, tenantId, permission, authorization, audit, ct) is { } denied) return denied;
        try
        {
            return await body();
        }
        catch (ValidationFailedException e)
        {
            return Results.BadRequest(new { error = e.Codes[0], codes = e.Codes });
        }
        catch (NotFoundException)
        {
            return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        }
    }
}
