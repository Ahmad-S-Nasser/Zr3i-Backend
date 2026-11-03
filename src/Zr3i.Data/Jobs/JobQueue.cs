using MongoDB.Driver;

namespace Zr3i.Data.Jobs;

/// <summary>The tenant-facing side of the queue: enqueue, look at, and cancel your own jobs. Every call needs a <see cref="TenantScope"/>.</summary>
public sealed class JobQueue(IMongoCollection<Job> collection)
{
    private readonly TenantCollection<Job> _rows = new(collection);

    public async Task<Job> EnqueueAsync(
        TenantScope scope, string type, string payloadJson, string createdBy, DateTime now, int maxAttempts = 3, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var job = new Job { Type = type, PayloadJson = payloadJson, CreatedBy = createdBy, CreatedAt = now, AvailableAt = now, MaxAttempts = maxAttempts };
        await _rows.InsertAsync(scope, job, ct).ConfigureAwait(false);
        return job;
    }

    public Task<Job?> GetAsync(TenantScope scope, string id, CancellationToken ct = default) => _rows.GetAsync(scope, id, ct);

    public Task<IReadOnlyList<Job>> ListAsync(TenantScope scope, string? type = null, int limit = 100, CancellationToken ct = default) =>
        type is null ? _rows.FindAsync(scope, _ => true, limit, ct) : _rows.FindAsync(scope, j => j.Type == type, limit, ct);

    /// <summary>Cancels a job that has not started. A running or finished job cannot be cancelled; returns false.</summary>
    public async Task<bool> CancelAsync(TenantScope scope, string id, DateTime now, CancellationToken ct = default)
    {
        var filter = Builders<Job>.Filter.And(_rows.ById(scope, id), Builders<Job>.Filter.Eq(j => j.Status, JobStatus.Queued));
        var update = Builders<Job>.Update.Set(j => j.Status, JobStatus.Cancelled).Set(j => j.FinishedAt, now);
        var result = await collection.UpdateOneAsync(filter, update, cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }
}
