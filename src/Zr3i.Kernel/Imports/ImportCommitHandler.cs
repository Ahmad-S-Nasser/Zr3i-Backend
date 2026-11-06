using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Notifications;

namespace Zr3i.Kernel.Imports;

/// <summary>
/// Applies the valid rows of a validated batch, in file order, a page at a time. Every row is marked applied (or failed) when its
/// page is done, so a job that is interrupted and run again carries on where it stopped and never applies a row twice. A row that
/// cannot be applied is reported and skipped; it does not stop the others.
/// </summary>
public sealed class ImportCommitHandler(KernelStore store, AuditService audit, TimeProvider clock, NotificationService? notify = null) : IJobHandler
{
    private const int Page = 500;

    public string Type => ImportService.CommitJobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var batchId = context.Payload<ImportJobPayload>().BatchId;
        var batch = await store.ImportBatches.GetAsync(scope, batchId, ct);
        if (batch is null) return JobOutcome.Failure("batch_not_found");
        if (batch.Status is not (ImportStatus.CommitQueued or ImportStatus.Committing)) return batch.Status == ImportStatus.Committed ? JobOutcome.Success() : JobOutcome.Failure("batch_not_available");

        batch.Status = ImportStatus.Committing;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);

        var definitions = await store.CustomFieldDefinitions.FindAsync(scope, _ => true, ct: ct);
        var validator = new ImportRowValidator(definitions);
        var applier = new ImportPageApplier(store, audit, clock, scope, batch.CreatedBy, batch);

        var total = batch.ValidRows;
        var done = total - (int)await store.ImportRows.CountAsync(scope, r => r.BatchId == batchId && r.Status == ImportRowStatus.Valid, ct);
        string? cursor = null;
        do
        {
            var page = await store.ImportRows.FindPageAsync(scope, r => r.BatchId == batchId && r.Status == ImportRowStatus.Valid, cursor, Page, ct);
            await ApplyPageAsync(scope, batch, validator, applier, page.Items, ct);
            done += page.Items.Count;
            await context.ReportProgressAsync(done, total, ct);
            cursor = page.NextCursor;
        } while (cursor is not null);

        // Totals come from the rows themselves, so they are right even after an interrupted run.
        batch.Created = await CountAsync(scope, batchId, ImportActions.Created, ct);
        batch.Updated = await CountAsync(scope, batchId, ImportActions.Updated, ct);
        batch.Unchanged = await CountAsync(scope, batchId, ImportActions.Unchanged, ct);
        batch.Failed = (int)await store.ImportRows.CountAsync(scope, r => r.BatchId == batchId && r.Status == ImportRowStatus.Failed, ct);
        batch.Status = ImportStatus.Committed;
        batch.CommittedAt = clock.GetUtcNow().UtcDateTime;
        await store.ImportBatches.ReplaceAsync(scope, batch, ct);
        await audit.RecordAsync(scope, batch.CreatedBy, "import.committed", "import_batch", batch.Id, ct: ct);
        if (notify is not null) await notify.NotifyAsync(scope, [batch.CreatedBy], NotificationCodes.ImportCommitted, "import_batch", batch.Id, batch.FileName, ct);
        await context.ReportProgressAsync(total, total, ct);

        return JobOutcome.Success(JobJson.Serialize(new { batch.Created, batch.Updated, batch.Unchanged, batch.Failed }));
    }

    private async Task ApplyPageAsync(
        TenantScope scope, ImportBatch batch, ImportRowValidator validator, ImportPageApplier applier, IReadOnlyList<ImportRow> rows, CancellationToken ct)
    {
        // Definitions may have changed since validation, so every row is checked again before it is applied.
        var work = new List<(ImportRow Row, ParsedRow Parsed)>();
        foreach (var row in rows)
        {
            var validation = validator.Validate(ImportValidateHandler.ToDictionary(batch.Header, row.Values));
            if (validation.Row is null) Fail(row, validation.Errors);
            else work.Add((row, validation.Row));
        }

        var outcomes = await ApplyWithFallbackAsync(applier, [.. work.Select(w => w.Parsed)], ct);
        for (var i = 0; i < work.Count; i++)
        {
            var (row, outcome) = (work[i].Row, outcomes[i]);
            if (outcome.Succeeded)
            {
                row.Action = outcome.Action;
                row.Status = ImportRowStatus.Applied;
                row.Errors = [];
            }
            else
            {
                Fail(row, [.. outcome.Errors.Select(c => new ImportRowError { Field = string.Empty, Code = c })]);
            }
        }

        await store.ImportRows.ReplaceManyAsync(scope, rows, ct);
    }

    /// <summary>
    /// Tries the whole page in bulk. If a unique key is hit (a concurrent import of the same ids), the page is redone one row at a time
    /// from a fresh read, so each row gets its own verdict and nothing already written is written twice.
    /// </summary>
    private static async Task<IReadOnlyList<RowOutcome>> ApplyWithFallbackAsync(ImportPageApplier applier, IReadOnlyList<ParsedRow> parsed, CancellationToken ct)
    {
        try
        {
            return await applier.ApplyAsync(parsed, ct);
        }
        catch (DuplicateKeyException)
        {
            applier.Reset();
            var outcomes = new List<RowOutcome>(parsed.Count);
            foreach (var row in parsed)
            {
                try
                {
                    outcomes.Add((await applier.ApplyAsync([row], ct))[0]);
                }
                catch (DuplicateKeyException)
                {
                    applier.Reset();
                    outcomes.Add(RowOutcome.Refused("concurrent_change"));
                }
            }

            return outcomes;
        }
    }

    private static void Fail(ImportRow row, IReadOnlyList<ImportRowError> errors)
    {
        row.Status = ImportRowStatus.Failed;
        row.Errors = [.. errors];
    }

    private async Task<int> CountAsync(TenantScope scope, string batchId, string action, CancellationToken ct) =>
        (int)await store.ImportRows.CountAsync(scope, r => r.BatchId == batchId && r.Action == action, ct);
}
