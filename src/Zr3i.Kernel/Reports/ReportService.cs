using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Reports;

/// <summary>A report that has been asked for but is not frozen yet, or whose freezing failed.</summary>
public sealed record PendingReport(string ReportId, string Kind, string? Season, string Status, string? ErrorCode, DateTime RequestedAt);

public sealed record ReportList(IReadOnlyList<FrozenReport> Ready, IReadOnlyList<PendingReport> Pending);

public sealed record ReportDownload(string Name, string ContentType, byte[] Data, string Sha256);

/// <summary>
/// The front door of reports. Nothing heavy happens here (hard rule 6): a request is checked and a freeze job is queued; the worker builds
/// the snapshot, renders the files and writes the frozen report. Reads and downloads see only the organisation's own reports.
/// </summary>
public sealed class ReportService(KernelStore store, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const string FreezeJobType = "reports.freeze";

    /// <summary>Queues a report. The id is chosen now so the person can follow it; the frozen report appears under that id when the worker is done.</summary>
    /// <exception cref="ValidationFailedException">unknown_kind, invalid_season, subject_required.</exception>
    /// <exception cref="NotFoundException">tranche, farm.</exception>
    public async Task<PendingReport> RequestAsync(TenantScope scope, string actorUserId, string? kind, string? subjectId, string? season, CancellationToken ct = default)
    {
        if (kind is null || !ReportKinds.All.Contains(kind)) throw new ValidationFailedException("unknown_kind");
        season = season?.Trim();
        var needsSeason = kind != ReportKinds.Tranche;
        if (needsSeason && !FarmRules.IsValidSeason(season ?? string.Empty)) throw new ValidationFailedException("invalid_season");
        if (kind != ReportKinds.FarmerResults && string.IsNullOrWhiteSpace(subjectId)) throw new ValidationFailedException("subject_required");

        // The subject must be one of this organisation's own; the repository filter makes another organisation's id look like no row.
        if (kind == ReportKinds.Tranche) _ = await store.Tranches.GetAsync(scope, subjectId!, ct) ?? throw new NotFoundException("tranche", subjectId!);
        if (kind == ReportKinds.FarmRisk) _ = await store.Farms.GetAsync(scope, subjectId!, ct) ?? throw new NotFoundException("farm", subjectId!);

        var reportId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;
        var payload = new ReportJobPayload(reportId, kind, kind == ReportKinds.FarmerResults ? null : subjectId, season, actorUserId);
        await jobs.EnqueueAsync(scope, FreezeJobType, JobJson.Serialize(payload), actorUserId, now, ct: ct);
        await audit.RecordAsync(scope, actorUserId, "report.requested", "report", reportId, purpose: kind, ct: ct);
        return new PendingReport(reportId, kind, season, JobStatus.Queued, null, now);
    }

    public async Task<ReportList> ListAsync(TenantScope scope, CancellationToken ct = default)
    {
        var ready = (await store.FrozenReports.FindAsync(scope, _ => true, 500, ct)).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id, StringComparer.Ordinal).ToList();
        var done = ready.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        var pending = new List<PendingReport>();
        foreach (var job in await jobs.ListAsync(scope, FreezeJobType, 500, ct))
        {
            if (job.Status == JobStatus.Cancelled) continue;
            var p = System.Text.Json.JsonSerializer.Deserialize<ReportJobPayload>(job.PayloadJson, JobJson.Options);
            if (p is null || done.Contains(p.ReportId) || job.Status == JobStatus.Succeeded) continue;
            pending.Add(new PendingReport(p.ReportId, p.Kind, p.Season, job.Status, job.ErrorCode, job.CreatedAt));
        }

        return new ReportList(ready, [.. pending.OrderByDescending(p => p.RequestedAt)]);
    }

    /// <exception cref="NotFoundException">report.</exception>
    public async Task<FrozenReport> GetAsync(TenantScope scope, string id, CancellationToken ct = default) =>
        await store.FrozenReports.GetAsync(scope, id, ct) ?? throw new NotFoundException("report", id);

    /// <summary>One file of a frozen report. Reading it is audited.</summary>
    /// <exception cref="NotFoundException">report, file.</exception>
    public async Task<ReportDownload> DownloadAsync(TenantScope scope, string actorUserId, string reportId, string name, CancellationToken ct = default)
    {
        _ = await GetAsync(scope, reportId, ct);
        var file = (await store.ReportFiles.FindAsync(scope, f => f.ReportId == reportId && f.Name == name, 1, ct)).FirstOrDefault() ?? throw new NotFoundException("file", name);
        await audit.RecordAsync(scope, actorUserId, "report.downloaded", "report", reportId, purpose: name, ct: ct);
        return new ReportDownload(file.Name, file.ContentType, file.Data, file.Sha256);
    }
}
