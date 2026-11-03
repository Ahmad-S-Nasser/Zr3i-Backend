using Zr3i.Data;
using Zr3i.Data.Jobs;

namespace Zr3i.Kernel.Jobs;

/// <summary>
/// Claims one job at a time, runs its handler, keeps the lease alive while it runs, and records the outcome.
/// If the lease is lost (the job was handed to another worker) the handler is cancelled and nothing is written.
/// </summary>
public sealed class JobRunner(JobClaimer claimer, IEnumerable<IJobHandler> handlers, TimeProvider clock, string workerId, TimeSpan? heartbeatInterval = null)
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    private readonly TimeSpan _heartbeat = heartbeatInterval ?? claimer.Options.LeaseDuration / 3;

    /// <returns>True if a job was claimed and handled; false if the queue had nothing for this worker.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var job = await claimer.ClaimNextAsync(workerId, Now(), ct).ConfigureAwait(false);
        if (job is null) return false;

        var scope = new JobContext(job, claimer, workerId).Scope;
        if (!_handlers.TryGetValue(job.Type, out var handler))
        {
            await claimer.FailAsync(scope, job.Id, workerId, "unknown_job_type", retry: false, Now(), ct: ct).ConfigureAwait(false);
            return true;
        }

        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = KeepLeaseAsync(scope, job.Id, ownership);
        JobOutcome outcome;
        try
        {
            outcome = await handler.HandleAsync(new JobContext(job, claimer, workerId), ownership.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;         // the host is shutting down; the lease will expire and another worker takes over
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
            return true;   // the lease was lost; the new owner records the outcome
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = JobOutcome.RetryLater("handler_exception:" + e.GetType().Name);
        }
        finally
        {
            await ownership.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        if (outcome.Succeeded) await claimer.CompleteAsync(scope, job.Id, workerId, outcome.ResultJson, Now(), CancellationToken.None).ConfigureAwait(false);
        else await claimer.FailAsync(scope, job.Id, workerId, outcome.ErrorCode ?? "failed", outcome.Retry, Now(), outcome.ResultJson, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>Runs jobs until the queue has nothing for this worker. For tests and one-off batch runs.</summary>
    public async Task<int> RunUntilIdleAsync(CancellationToken ct = default)
    {
        var count = 0;
        while (await RunOnceAsync(ct).ConfigureAwait(false)) count++;
        return count;
    }

    private async Task KeepLeaseAsync(TenantScope scope, string jobId, CancellationTokenSource ownership)
    {
        using var timer = new PeriodicTimer(_heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ownership.Token).ConfigureAwait(false))
            {
                if (!await claimer.HeartbeatAsync(scope, jobId, workerId, Now(), CancellationToken.None).ConfigureAwait(false))
                {
                    await ownership.CancelAsync().ConfigureAwait(false);   // not ours any more: stop the handler
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the handler finished first
        }
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
