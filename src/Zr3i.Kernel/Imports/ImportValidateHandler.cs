using System.Text;
using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Notifications;

namespace Zr3i.Kernel.Imports;

/// <summary>
/// Reads the uploaded file, checks every row and stages the result so a person can see the report before committing.
/// Safe to run again for the same batch: staged rows are replaced, never doubled.
/// </summary>
public sealed class ImportValidateHandler(KernelStore store, AuditService audit, TimeProvider clock, NotificationService? notify = null) : IJobHandler
{
    public const int MaxRows = 100_000;
    private const int InsertBatch = 1000;

    public string Type => ImportService.ValidateJobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var batchId = context.Payload<ImportJobPayload>().BatchId;
        var batch = await store.ImportBatches.GetAsync(scope, batchId, ct);
        if (batch is null) return JobOutcome.Failure("batch_not_found");
        if (batch.Status is not (ImportStatus.Validating or ImportStatus.Uploaded or ImportStatus.Validated or ImportStatus.Failed)) return JobOutcome.Failure("batch_not_available");

        await store.ImportRows.DeleteWhereAsync(scope, r => r.BatchId == batchId, ct);
        batch.FileErrors = [];
        batch.FileWarnings = [];
        batch.Header = [];
        batch.TotalRows = batch.ValidRows = batch.InvalidRows = 0;

        var text = await ReadFileAsync(scope, batchId, batch.FileErrors, ct);
        if (text is null) return await FailFileAsync(scope, batch, ct);

        var parsed = CsvReader.Parse(text);
        if (parsed.Error is not null) { batch.FileErrors.Add($"{parsed.Error}:line {parsed.ErrorLine}"); return await FailFileAsync(scope, batch, ct); }
        if (parsed.Records.Count == 0) { batch.FileErrors.Add("empty_file"); return await FailFileAsync(scope, batch, ct); }

        var definitions = await store.CustomFieldDefinitions.FindAsync(scope, _ => true, ct: ct);
        var header = parsed.Records[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        batch.Header = header;
        CheckHeader(header, definitions, batch);
        if (parsed.Records.Count == 1) batch.FileErrors.Add("no_rows");
        if (parsed.Records.Count - 1 > MaxRows) batch.FileErrors.Add("too_many_rows");
        if (batch.FileErrors.Count > 0) return await FailFileAsync(scope, batch, ct);

        var validator = new ImportRowValidator(definitions);
        var cross = new CrossRowChecker();
        var staged = new List<ImportRow>(InsertBatch);
        var total = parsed.Records.Count - 1;

        for (var i = 1; i < parsed.Records.Count; i++)
        {
            var record = parsed.Records[i];
            var row = new ImportRow { BatchId = batchId, RowNumber = record.Line, Values = [.. record.Fields] };

            if (record.Fields.Length != header.Count)
            {
                row.Errors.Add(new ImportRowError { Field = string.Empty, Code = "wrong_column_count" });
            }
            else
            {
                var result = validator.Validate(ToDictionary(header, record.Fields));
                row.Errors.AddRange(result.Errors);
                if (result.Row is not null) row.Errors.AddRange(cross.Check(result.Row));
            }

            row.Status = row.Errors.Count == 0 ? ImportRowStatus.Valid : ImportRowStatus.Invalid;
            if (row.Status == ImportRowStatus.Valid) batch.ValidRows++; else batch.InvalidRows++;
            staged.Add(row);

            if (staged.Count == InsertBatch)
            {
                await store.ImportRows.InsertManyAsync(scope, staged, ct);
                staged.Clear();
                await context.ReportProgressAsync(i, total, ct);
            }
        }

        if (staged.Count > 0) await store.ImportRows.InsertManyAsync(scope, staged, ct);
        batch.TotalRows = total;
        batch.Status = ImportStatus.Validated;
        batch.ValidatedAt = clock.GetUtcNow().UtcDateTime;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await audit.RecordAsync(scope, batch.CreatedBy, "import.validated", "import_batch", batch.Id, ct: ct);
        if (notify is not null) await notify.NotifyAsync(scope, [batch.CreatedBy], NotificationCodes.ImportValidated, "import_batch", batch.Id, batch.FileName, ct);
        await context.ReportProgressAsync(total, total, ct);

        return JobOutcome.Success(JobJson.Serialize(new { batch.TotalRows, batch.ValidRows, batch.InvalidRows }));
    }

    /// <summary>The header must have the required columns, no repeats, and only custom columns the organisation has defined.</summary>
    private static void CheckHeader(List<string> header, IReadOnlyList<CustomFieldDefinition> definitions, ImportBatch batch)
    {
        foreach (var column in ImportColumns.Required.Where(r => !header.Contains(r))) batch.FileErrors.Add($"missing_column:{column}");
        foreach (var dup in header.GroupBy(h => h).Where(g => g.Count() > 1 && g.Key.Length > 0)) batch.FileErrors.Add($"duplicate_column:{dup.Key}");
        if (header.Any(h => h.Length == 0)) batch.FileErrors.Add("blank_column_name");

        foreach (var column in header.Where(h => h.Length > 0).Distinct())
        {
            var prefix = ImportColumns.CustomPrefixes.Keys.FirstOrDefault(p => column.StartsWith(p, StringComparison.Ordinal));
            if (prefix is not null)
            {
                var entity = ImportColumns.CustomPrefixes[prefix];
                if (!definitions.Any(d => d.Entity == entity && d.Key == column[prefix.Length..])) batch.FileErrors.Add($"unknown_custom_field:{column}");
            }
            else if (!ImportColumns.Known.Contains(column))
            {
                batch.FileWarnings.Add($"unknown_column:{column}");
            }
        }
    }

    private async Task<string?> ReadFileAsync(TenantScope scope, string batchId, List<string> fileErrors, CancellationToken ct)
    {
        var chunks = (await store.ImportChunks.FindAsync(scope, c => c.BatchId == batchId, ct: ct)).OrderBy(c => c.Seq).ToList();
        if (chunks.Count == 0) { fileErrors.Add("empty_file"); return null; }

        var bytes = new byte[chunks.Sum(c => c.Data.Length)];
        var offset = 0;
        foreach (var chunk in chunks) { chunk.Data.CopyTo(bytes, offset); offset += chunk.Data.Length; }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            fileErrors.Add("not_utf8");
            return null;
        }
    }

    private async Task<JobOutcome> FailFileAsync(TenantScope scope, ImportBatch batch, CancellationToken ct)
    {
        batch.Status = ImportStatus.Failed;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await audit.RecordAsync(scope, batch.CreatedBy, "import.rejected", "import_batch", batch.Id, AuditService.Denied, ct: ct);
        if (notify is not null) await notify.NotifyAsync(scope, [batch.CreatedBy], NotificationCodes.ImportRejected, "import_batch", batch.Id, batch.FileName, ct);
        return JobOutcome.Failure("bad_file", JobJson.Serialize(new { errors = batch.FileErrors }));
    }

    internal static Dictionary<string, string> ToDictionary(IReadOnlyList<string> header, IReadOnlyList<string> values)
    {
        var d = new Dictionary<string, string>(header.Count);
        for (var i = 0; i < header.Count; i++) d[header[i]] = values[i];
        return d;
    }
}
