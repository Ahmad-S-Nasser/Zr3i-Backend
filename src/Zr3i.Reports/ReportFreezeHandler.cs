using System.Text;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Notifications;
using Zr3i.Kernel.Reports;

namespace Zr3i.Reports;

/// <summary>
/// Freezes a report: builds the snapshot, renders English and Arabic pages, the CSV and the PDFs, hashes every file, writes a manifest and stores it all.
/// Safe to run twice for the same job: a report that already exists is left as it is, and files written by an interrupted run are reused.
/// </summary>
public sealed class ReportFreezeHandler(KernelStore store, SnapshotBuilder builder, IPdfRenderer pdf, AuditService audit, TimeProvider clock, NotificationService? notify = null) : IJobHandler
{
    public string Type => ReportService.FreezeJobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var payload = context.Payload<ReportJobPayload>();
        if (await store.FrozenReports.GetAsync(scope, payload.ReportId, ct) is not null) return JobOutcome.Success(JobJson.Serialize(new { reportId = payload.ReportId }));

        ReportSnapshot snapshot;
        try { snapshot = await builder.BuildAsync(scope, payload, ct); }
        catch (NotFoundException) { return await FailedAsync(scope, payload, "subject_not_found"); }
        catch (ValidationFailedException e) { return await FailedAsync(scope, payload, e.Codes.FirstOrDefault() ?? "invalid_request"); }

        var json = snapshot.ToJson();
        var snapshotSha = snapshot.Sha256();
        var created = clock.GetUtcNow().UtcDateTime;

        var htmlEn = Encoding.UTF8.GetBytes(ReportHtml.Render(snapshot, "en", snapshotSha));
        var htmlAr = Encoding.UTF8.GetBytes(ReportHtml.Render(snapshot, "ar", snapshotSha));
        byte[] pdfEn, pdfAr;
        try
        {
            pdfEn = await pdf.RenderAsync(Encoding.UTF8.GetString(htmlEn), created, ct);
            pdfAr = await pdf.RenderAsync(Encoding.UTF8.GetString(htmlAr), created, ct);
        }
        catch (InvalidOperationException e) when (e.Message == "browser_not_configured") { return await FailedAsync(scope, payload, "browser_not_configured"); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A retry is not a failure yet; the person is told only when the attempts run out.
            if (context.Job.Attempts < context.Job.MaxAttempts) return JobOutcome.RetryLater("pdf_unavailable");
            return await FailedAsync(scope, payload, "pdf_unavailable");
        }

        var files = new List<ReportFile>
        {
            File(payload.ReportId, "report.en.html", "text/html; charset=utf-8", htmlEn),
            File(payload.ReportId, "report.ar.html", "text/html; charset=utf-8", htmlAr),
            File(payload.ReportId, "report.en.pdf", "application/pdf", pdfEn),
            File(payload.ReportId, "report.ar.pdf", "application/pdf", pdfAr),
            File(payload.ReportId, "report.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(ReportCsv.Render(snapshot))),
            File(payload.ReportId, "snapshot.json", "application/json", Encoding.UTF8.GetBytes(json)),
        };

        // A run that was interrupted may already have stored some files; those are the ones the manifest must describe.
        var existing = (await store.ReportFiles.FindAsync(scope, f => f.ReportId == payload.ReportId, 50, ct)).ToDictionary(f => f.Name, StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++) if (existing.TryGetValue(files[i].Name, out var kept)) files[i] = kept;

        var manifest = File(payload.ReportId, "manifest.json", "application/json",
            Encoding.UTF8.GetBytes(ReportManifest.Render(payload.ReportId, snapshot.Kind, snapshotSha, created, files.Select(f => new ReportManifest.Entry(f.Name, f.Data.Length, f.Sha256)))));
        files.Add(existing.TryGetValue(manifest.Name, out var keptManifest) ? keptManifest : manifest);
        manifest = files[^1];

        foreach (var f in files.Where(f => !existing.ContainsKey(f.Name))) await store.ReportFiles.InsertAsync(scope, f, ct);

        var report = new FrozenReport
        {
            Id = payload.ReportId,
            Kind = snapshot.Kind,
            Season = snapshot.Season,
            SubjectId = payload.SubjectId,
            SubjectName = snapshot.Subject,
            SnapshotJson = json,
            SnapshotSha256 = snapshotSha,
            MethodVersions = snapshot.MethodId is null ? string.Empty : $"{snapshot.MethodId}/{snapshot.MethodVersion}",
            Synthetic = snapshot.Synthetic,
            CreatedBy = payload.RequestedBy,
            CreatedAt = created,
            Files = [.. files.Select(f => new ReportFileInfo { Name = f.Name, ContentType = f.ContentType, Bytes = f.Data.Length, Sha256 = f.Sha256 })],
            ManifestSha256 = manifest.Sha256,
        };
        await store.FrozenReports.InsertAsync(scope, report, ct);
        await audit.RecordAsync(scope, payload.RequestedBy, "report.frozen", "report", report.Id, purpose: snapshot.Kind, ct: ct);
        if (notify is not null) await notify.NotifyAsync(scope, [payload.RequestedBy], NotificationCodes.ReportReady, "report", report.Id, snapshot.Kind, ct);
        return JobOutcome.Success(JobJson.Serialize(new { reportId = report.Id, snapshotSha256 = snapshotSha }));
    }

    private async Task<JobOutcome> FailedAsync(TenantScope scope, ReportJobPayload payload, string code)
    {
        if (notify is not null) await notify.NotifyAsync(scope, [payload.RequestedBy], NotificationCodes.ReportFailed, "report", payload.ReportId, payload.Kind);
        return JobOutcome.Failure(code);
    }

    private static ReportFile File(string reportId, string name, string contentType, byte[] data) =>
        new() { ReportId = reportId, Name = name, ContentType = contentType, Data = data, Sha256 = ReportSnapshot.Hash(data) };
}
