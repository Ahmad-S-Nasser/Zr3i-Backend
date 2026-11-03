using MongoDB.Bson;
using MongoDB.Driver;

namespace Zr3i.Data.Jobs;

/// <summary>
/// The worker side of the queue. <see cref="ClaimNextAsync"/> is the one deliberate cross-tenant read in the system:
/// it chooses which tenant is served next so that no tenant can starve the others. Only the worker host uses this
/// class (an architecture test enforces it); every other call here is scoped to the job's tenant.
/// </summary>
public sealed class JobClaimer
{
    private readonly IMongoCollection<Job> _jobs;
    private readonly IMongoCollection<BsonDocument> _raw;
    private readonly JobQueueOptions _options;

    internal JobClaimer(IMongoDatabase database, JobQueueOptions options)
    {
        _jobs = database.GetCollection<Job>(Collections.Jobs);
        _raw = database.GetCollection<BsonDocument>(Collections.Jobs);
        _options = options;
    }

    public JobQueueOptions Options => _options;

    /// <summary>
    /// Picks the tenant with the fewest running jobs (ties go to the tenant waiting longest), skips tenants at their cap,
    /// and atomically takes that tenant's oldest eligible job. Eligible: queued and due, or running with an expired lease
    /// and attempts left. Returns null when there is nothing to do.
    /// </summary>
    public async Task<Job?> ClaimNextAsync(string workerId, DateTime now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await FailExhaustedAsync(now, ct).ConfigureAwait(false);

        var eligible = Eligible(now);
        var waiting = await _raw.Aggregate()
            .Match(eligible)
            .Group(new BsonDocument { { "_id", "$TenantId" }, { "oldest", new BsonDocument("$min", "$CreatedAt") } })
            .ToListAsync(ct).ConfigureAwait(false);
        if (waiting.Count == 0) return null;

        var running = (await _raw.Aggregate()
            .Match(new BsonDocument { { "Status", JobStatus.Running }, { "LockedUntil", new BsonDocument("$gte", now) } })
            .Group(new BsonDocument { { "_id", "$TenantId" }, { "n", new BsonDocument("$sum", 1) } })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(d => d["_id"].AsString, d => d["n"].ToInt32());

        var candidates = waiting
            .Select(d => (Tenant: d["_id"].AsString, Running: running.GetValueOrDefault(d["_id"].AsString), Oldest: d["oldest"].ToUniversalTime()))
            .Where(c => c.Running < _options.MaxRunningPerTenant)
            .OrderBy(c => c.Running).ThenBy(c => c.Oldest);

        foreach (var candidate in candidates)
        {
            var filter = Builders<Job>.Filter.And(Builders<Job>.Filter.Eq(j => j.TenantId, candidate.Tenant), new BsonDocumentFilterDefinition<Job>(eligible));
            var update = Builders<Job>.Update
                .Set(j => j.Status, JobStatus.Running)
                .Set(j => j.LockedBy, workerId)
                .Set(j => j.LockedUntil, now + _options.LeaseDuration)
                .Set(j => j.StartedAt, now)
                .Inc(j => j.Attempts, 1);
            var claimed = await _jobs.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<Job> { Sort = Builders<Job>.Sort.Ascending(j => j.CreatedAt), ReturnDocument = ReturnDocument.After }, ct)
                .ConfigureAwait(false);
            if (claimed is not null) return claimed;   // otherwise another worker was faster; try the next tenant
        }

        return null;
    }

    /// <summary>Extends the lease. False means the job is no longer this worker's and it should stop.</summary>
    public async Task<bool> HeartbeatAsync(TenantScope scope, string jobId, string workerId, DateTime now, CancellationToken ct = default) =>
        await UpdateOwnedAsync(scope, jobId, workerId, Builders<Job>.Update.Set(j => j.LockedUntil, now + _options.LeaseDuration), ct).ConfigureAwait(false);

    public async Task<bool> ReportProgressAsync(TenantScope scope, string jobId, string workerId, int done, int total, CancellationToken ct = default) =>
        await UpdateOwnedAsync(scope, jobId, workerId, Builders<Job>.Update.Set(j => j.ProgressDone, done).Set(j => j.ProgressTotal, total), ct).ConfigureAwait(false);

    public async Task<bool> CompleteAsync(TenantScope scope, string jobId, string workerId, string? resultJson, DateTime now, CancellationToken ct = default) =>
        await UpdateOwnedAsync(scope, jobId, workerId, Builders<Job>.Update
            .Set(j => j.Status, JobStatus.Succeeded).Set(j => j.ResultJson, resultJson).Set(j => j.FinishedAt, now)
            .Set(j => j.LockedBy, null).Set(j => j.LockedUntil, null), ct).ConfigureAwait(false);

    /// <summary>
    /// Records a failure. With <paramref name="retry"/> and attempts left the job goes back to the queue after a delay;
    /// otherwise it is failed for good (<paramref name="resultJson"/> may carry the details).
    /// </summary>
    public async Task<bool> FailAsync(
        TenantScope scope, string jobId, string workerId, string errorCode, bool retry, DateTime now, string? resultJson = null, CancellationToken ct = default)
    {
        var job = await _jobs.Find(Owned(scope, jobId, workerId)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (job is null) return false;

        var update = retry && job.Attempts < job.MaxAttempts
            ? Builders<Job>.Update.Set(j => j.Status, JobStatus.Queued).Set(j => j.AvailableAt, now + _options.RetryBackoff * job.Attempts)
                .Set(j => j.ErrorCode, errorCode).Set(j => j.LockedBy, null).Set(j => j.LockedUntil, null)
            : Builders<Job>.Update.Set(j => j.Status, JobStatus.Failed).Set(j => j.FinishedAt, now).Set(j => j.ErrorCode, errorCode)
                .Set(j => j.ResultJson, resultJson).Set(j => j.LockedBy, null).Set(j => j.LockedUntil, null);
        return await UpdateOwnedAsync(scope, jobId, workerId, update, ct).ConfigureAwait(false);
    }

    private static FilterDefinition<Job> Owned(TenantScope scope, string jobId, string workerId) =>
        Builders<Job>.Filter.And(
            Builders<Job>.Filter.Eq(j => j.TenantId, scope.TenantId),
            Builders<Job>.Filter.Eq(j => j.Id, jobId),
            Builders<Job>.Filter.Eq(j => j.Status, JobStatus.Running),
            Builders<Job>.Filter.Eq(j => j.LockedBy, workerId));

    private async Task<bool> UpdateOwnedAsync(TenantScope scope, string jobId, string workerId, UpdateDefinition<Job> update, CancellationToken ct)
    {
        var result = await _jobs.UpdateOneAsync(Owned(scope, jobId, workerId), update, cancellationToken: ct).ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    private static BsonDocument Eligible(DateTime now) => new("$or", new BsonArray
    {
        new BsonDocument { { "Status", JobStatus.Queued }, { "AvailableAt", new BsonDocument("$lte", now) } },
        new BsonDocument
        {
            { "Status", JobStatus.Running },
            { "LockedUntil", new BsonDocument("$lt", now) },
            { "$expr", new BsonDocument("$lt", new BsonArray { "$Attempts", "$MaxAttempts" }) },
        },
    });

    /// <summary>A job whose worker vanished on its last allowed attempt will never be retried; fail it instead of leaving it "running".</summary>
    private async Task FailExhaustedAsync(DateTime now, CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            { "Status", JobStatus.Running },
            { "LockedUntil", new BsonDocument("$lt", now) },
            { "$expr", new BsonDocument("$gte", new BsonArray { "$Attempts", "$MaxAttempts" }) },
        };
        var update = Builders<Job>.Update.Set(j => j.Status, JobStatus.Failed).Set(j => j.ErrorCode, "lease_expired")
            .Set(j => j.FinishedAt, now).Set(j => j.LockedBy, null).Set(j => j.LockedUntil, null);
        await _jobs.UpdateManyAsync(new BsonDocumentFilterDefinition<Job>(filter), update, cancellationToken: ct).ConfigureAwait(false);
    }
}
