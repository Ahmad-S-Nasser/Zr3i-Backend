using System.Security.Cryptography;
using System.Text;
using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Imports;

public sealed record ImportView(ImportBatch Batch, int ProgressDone, int ProgressTotal, string? JobStatus);

/// <summary>
/// The front door of the bulk import. Nothing heavy happens here: an upload is stored and a validate job is queued;
/// a commit queues a commit job. The worker does the parsing, checking and writing (see the two handlers).
/// </summary>
public sealed class ImportService(KernelStore store, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const int MaxBytes = 25 * 1024 * 1024;
    public const int ChunkBytes = 1024 * 1024;
    public const string ValidateJobType = "import.validate";
    public const string CommitJobType = "import.commit";

    /// <exception cref="ValidationFailedException">"empty_file" or "file_too_large".</exception>
    public async Task<ImportBatch> UploadAsync(TenantScope scope, string actorUserId, string? fileName, Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(block, ct)) > 0)
        {
            buffer.Write(block, 0, read);
            if (buffer.Length > MaxBytes) throw new ValidationFailedException("file_too_large");
        }

        if (buffer.Length == 0) throw new ValidationFailedException("empty_file");
        var bytes = buffer.ToArray();
        var now = clock.GetUtcNow().UtcDateTime;

        var batch = new ImportBatch
        {
            FileName = CleanFileName(fileName),
            SizeBytes = bytes.Length,
            FileSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Status = ImportStatus.Validating,
            CreatedBy = actorUserId,
            CreatedAt = now,
        };
        await store.ImportBatches.InsertAsync(scope, batch, ct);

        for (int seq = 0, offset = 0; offset < bytes.Length; seq++, offset += ChunkBytes)
            await store.ImportChunks.InsertAsync(scope, new ImportChunk { BatchId = batch.Id, Seq = seq, Data = bytes[offset..Math.Min(offset + ChunkBytes, bytes.Length)] }, ct);

        var job = await jobs.EnqueueAsync(scope, ValidateJobType, JobJson.Serialize(new ImportJobPayload(batch.Id)), actorUserId, now, ct: ct);
        batch.ValidateJobId = job.Id;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await audit.RecordAsync(scope, actorUserId, "import.uploaded", "import_batch", batch.Id, ct: ct);
        return batch;
    }

    /// <summary>
    /// Queues the commit of a validated batch. Safe to call twice: a batch that is already queued, running or done
    /// is returned as it is. Invalid rows are skipped and reported; only valid rows are applied.
    /// </summary>
    public async Task<ImportBatch> CommitAsync(TenantScope scope, string actorUserId, string batchId, CancellationToken ct = default)
    {
        var batch = await Require(scope, batchId, ct);
        if (batch.Status is ImportStatus.CommitQueued or ImportStatus.Committing or ImportStatus.Committed) return batch;
        if (batch.Status != ImportStatus.Validated) throw new ValidationFailedException("not_validated");
        if (batch.ValidRows == 0) throw new ValidationFailedException("nothing_to_commit");

        var job = await jobs.EnqueueAsync(scope, CommitJobType, JobJson.Serialize(new ImportJobPayload(batch.Id)), actorUserId, clock.GetUtcNow().UtcDateTime, ct: ct);
        batch.CommitJobId = job.Id;
        batch.Status = ImportStatus.CommitQueued;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await audit.RecordAsync(scope, actorUserId, "import.commit_requested", "import_batch", batch.Id, ct: ct);
        return batch;
    }

    /// <summary>Throws the batch away before it is committed: the staged rows go, the uploaded file and the batch record stay.</summary>
    public async Task<ImportBatch> DiscardAsync(TenantScope scope, string actorUserId, string batchId, CancellationToken ct = default)
    {
        var batch = await Require(scope, batchId, ct);
        if (batch.Status is ImportStatus.Discarded) return batch;
        if (batch.Status is ImportStatus.CommitQueued or ImportStatus.Committing or ImportStatus.Committed) throw new ValidationFailedException("cannot_discard");

        if (batch.ValidateJobId is { } jobId) await jobs.CancelAsync(scope, jobId, clock.GetUtcNow().UtcDateTime, ct);
        batch.Status = ImportStatus.Discarded;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await store.ImportRows.DeleteWhereAsync(scope, r => r.BatchId == batch.Id, ct);
        await audit.RecordAsync(scope, actorUserId, "import.discarded", "import_batch", batch.Id, ct: ct);
        return batch;
    }

    public async Task<ImportView> GetAsync(TenantScope scope, string batchId, CancellationToken ct = default)
    {
        var batch = await Require(scope, batchId, ct);
        var jobId = batch.Status is ImportStatus.CommitQueued or ImportStatus.Committing or ImportStatus.Committed ? batch.CommitJobId : batch.ValidateJobId;
        var job = jobId is null ? null : await jobs.GetAsync(scope, jobId, ct);
        return new ImportView(batch, job?.ProgressDone ?? 0, job?.ProgressTotal ?? 0, job?.Status);
    }

    /// <summary>Newest first would need a sort the repository does not offer; the list is small, so it is ordered here.</summary>
    public async Task<IReadOnlyList<ImportBatch>> ListAsync(TenantScope scope, int limit = 100, CancellationToken ct = default) =>
        [.. (await store.ImportBatches.FindAsync(scope, _ => true, limit, ct)).OrderByDescending(b => b.CreatedAt)];

    /// <summary>Rows that were refused at validation or failed on commit, in file order, one page at a time.</summary>
    public async Task<Page<ImportRow>> ErrorsPageAsync(TenantScope scope, string batchId, string? cursor, int pageSize, CancellationToken ct = default)
    {
        await Require(scope, batchId, ct);
        return await store.ImportRows.FindPageAsync(scope, r => r.BatchId == batchId && (r.Status == ImportRowStatus.Invalid || r.Status == ImportRowStatus.Failed), cursor, pageSize, ct);
    }

    /// <summary>The error report as CSV: line, parcel and farm ids, column, code. Cells that could be read as a formula are defused.</summary>
    public async Task<string> ErrorReportCsvAsync(TenantScope scope, string batchId, CancellationToken ct = default)
    {
        var batch = await Require(scope, batchId, ct);
        var parcelAt = batch.Header.IndexOf(ImportColumns.ParcelExternalId);
        var farmAt = batch.Header.IndexOf(ImportColumns.FarmExternalId);
        string Cell(ImportRow r, int at) => at >= 0 && at < r.Values.Count ? r.Values[at] : string.Empty;

        var csv = new StringBuilder("line,parcel_external_id,farm_external_id,column,code\r\n");
        foreach (var e in batch.FileErrors) csv.Append("0,,,file,").Append(CsvCell(e)).Append("\r\n");

        string? cursor = null;
        do
        {
            var page = await store.ImportRows.FindPageAsync(scope,
                r => r.BatchId == batchId && (r.Status == ImportRowStatus.Invalid || r.Status == ImportRowStatus.Failed), cursor, 1000, ct);
            foreach (var row in page.Items)
                foreach (var error in row.Errors)
                    csv.Append(row.RowNumber).Append(',').Append(CsvCell(Cell(row, parcelAt))).Append(',').Append(CsvCell(Cell(row, farmAt)))
                       .Append(',').Append(CsvCell(error.Field)).Append(',').Append(CsvCell(error.Code)).Append("\r\n");
            cursor = page.NextCursor;
        } while (cursor is not null);

        return csv.ToString();
    }

    private async Task<ImportBatch> Require(TenantScope scope, string batchId, CancellationToken ct) =>
        await store.ImportBatches.GetAsync(scope, batchId ?? string.Empty, ct) ?? throw new NotFoundException("import", batchId ?? string.Empty);

    private static string CleanFileName(string? name)
    {
        var n = Path.GetFileName((name ?? string.Empty).Trim().Replace('\\', '/'));
        n = new string([.. n.Where(c => !char.IsControl(c))]);
        if (n.Length == 0) return "upload.csv";
        return n.Length > 200 ? n[..200] : n;
    }

    internal static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;   // spreadsheet formula injection
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
