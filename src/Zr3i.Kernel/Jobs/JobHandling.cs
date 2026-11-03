using System.Text.Json;
using Zr3i.Data;
using Zr3i.Data.Jobs;

namespace Zr3i.Kernel.Jobs;

/// <summary>What a handler reports. Failure is final; RetryLater puts the job back (until its attempts run out).</summary>
public sealed record JobOutcome(bool Succeeded, string? ResultJson, string? ErrorCode, bool Retry)
{
    public static JobOutcome Success(string? resultJson = null) => new(true, resultJson, null, false);

    public static JobOutcome Failure(string errorCode, string? resultJson = null) => new(false, resultJson, errorCode, false);

    public static JobOutcome RetryLater(string errorCode) => new(false, null, errorCode, true);
}

/// <summary>What a handler gets: the job, its tenant scope, its payload and a way to report progress.</summary>
public sealed class JobContext(Job job, JobClaimer claimer, string workerId)
{
    public Job Job { get; } = job;

    /// <summary>Everything the handler does must stay inside this scope.</summary>
    public TenantScope Scope { get; } = TenantScope.ForTenant(job.TenantId);

    public T Payload<T>() =>
        JsonSerializer.Deserialize<T>(Job.PayloadJson, JobJson.Options) ?? throw new InvalidOperationException("The job payload is empty.");

    public Task ReportProgressAsync(int done, int total, CancellationToken ct = default) =>
        claimer.ReportProgressAsync(Scope, Job.Id, workerId, done, total, ct);
}

public static class JobJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>Does the work for one job type. Handlers must be safe to run twice for the same job (a lease can expire mid-run).</summary>
public interface IJobHandler
{
    string Type { get; }

    Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct);
}
