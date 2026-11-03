namespace Zr3i.Data.Jobs;

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One unit of background work (hard rule 6: nothing heavy runs in a web request). The payload and result are JSON text
/// so neither the queue nor its callers depend on a document model. A running job holds a lease; a worker that dies
/// loses it and another worker picks the job up.
/// </summary>
public sealed class Job : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Not claimable before this time (used for the delay between retries).</summary>
    public DateTime AvailableAt { get; set; }

    /// <summary>Start of the latest attempt.</summary>
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
}

/// <param name="LeaseDuration">How long a worker owns a job without a heartbeat.</param>
/// <param name="MaxRunningPerTenant">No tenant may occupy more workers than this at once.</param>
/// <param name="RetryBackoff">Delay before a retry is the backoff times the number of attempts so far.</param>
public sealed record JobQueueOptions(TimeSpan LeaseDuration, int MaxRunningPerTenant, TimeSpan RetryBackoff)
{
    public static JobQueueOptions Default { get; } = new(TimeSpan.FromMinutes(2), 2, TimeSpan.FromSeconds(30));
}
