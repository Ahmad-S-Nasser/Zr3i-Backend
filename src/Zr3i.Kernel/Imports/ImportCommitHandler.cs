using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Imports;

/// <summary>
/// Applies the valid rows of a validated batch, in file order. Every row is marked applied (or failed) the moment it is done,
/// so a job that is interrupted and run again carries on where it stopped and never applies a row twice. A row that cannot be
/// applied is reported and skipped; it does not stop the others.
/// </summary>
public sealed class ImportCommitHandler(KernelStore store, BoundaryService boundaries, AuditService audit, TimeProvider clock) : IJobHandler
{
    private const int Page = 500;
    private const int ProgressEvery = 100;

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
        var applier = new ImportRowApplier(store, boundaries, audit, clock, scope, batch.CreatedBy, batch);

        var total = batch.ValidRows;
        var done = total - (int)await store.ImportRows.CountAsync(scope, r => r.BatchId == batchId && r.Status == ImportRowStatus.Valid, ct);
        string? cursor = null;
        do
        {
            var page = await store.ImportRows.FindPageAsync(scope, r => r.BatchId == batchId && r.Status == ImportRowStatus.Valid, cursor, Page, ct);
            foreach (var row in page.Items)
            {
                await ApplyRowAsync(scope, batch, validator, applier, row, ct);
                if (++done % ProgressEvery == 0) await context.ReportProgressAsync(done, total, ct);
            }

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
        await context.ReportProgressAsync(total, total, ct);

        return JobOutcome.Success(JobJson.Serialize(new { batch.Created, batch.Updated, batch.Unchanged, batch.Failed }));
    }

    private async Task ApplyRowAsync(TenantScope scope, ImportBatch batch, ImportRowValidator validator, ImportRowApplier applier, ImportRow row, CancellationToken ct)
    {
        var validation = validator.Validate(ImportValidateHandler.ToDictionary(batch.Header, row.Values));   // definitions may have changed since validation
        if (validation.Row is null)
        {
            Fail(row, validation.Errors);
        }
        else
        {
            try
            {
                row.Action = await applier.ApplyAsync(validation.Row, ct);
                row.Status = ImportRowStatus.Applied;
                row.Errors = [];
            }
            catch (ValidationFailedException e)
            {
                Fail(row, [.. e.Codes.Select(c => new ImportRowError { Field = string.Empty, Code = c })]);
            }
            catch (DuplicateKeyException)
            {
                Fail(row, [new ImportRowError { Field = string.Empty, Code = "concurrent_change" }]);
            }
        }

        await store.ImportRows.ReplaceAsync(scope, row, ct);
    }

    private static void Fail(ImportRow row, IReadOnlyList<ImportRowError> errors)
    {
        row.Status = ImportRowStatus.Failed;
        row.Errors = [.. errors];
    }

    private async Task<int> CountAsync(TenantScope scope, string batchId, string action, CancellationToken ct) =>
        (int)await store.ImportRows.CountAsync(scope, r => r.BatchId == batchId && r.Action == action, ct);
}
