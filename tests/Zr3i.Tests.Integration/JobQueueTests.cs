using Zr3i.Data;
using Zr3i.Data.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class JobQueueTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("jobs-tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("jobs-tenant-b");
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Queue(DataStore Data, JobClaimer Claimer)
    {
        public JobQueue Jobs => Data.Jobs;
    }

    private Queue New(JobQueueOptions? options = null)
    {
        var db = mongo.Database.Client.GetDatabase("zr3i_test_jobs_" + Guid.NewGuid().ToString("N"));
        var data = new DataStore(db);
        return new Queue(data, data.CreateJobClaimer(options ?? new JobQueueOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.FromSeconds(30))));
    }

    private static Task<Job> Enqueue(Queue q, TenantScope scope, DateTime at, string type = "test", int maxAttempts = 3) =>
        q.Jobs.EnqueueAsync(scope, type, "{}", "u1", at, maxAttempts);

    [Fact]
    public async Task EnqueuedJobsAreQueuedStampedWithTheTenantAndVisibleOnlyToIt()
    {
        var q = New();
        var job = await q.Jobs.EnqueueAsync(A, "import.validate", "{\"x\":1}", "u1", T0);

        Assert.Equal(("queued", "jobs-tenant-a", 0, T0), (job.Status, job.TenantId, job.Attempts, job.AvailableAt));
        Assert.Equal("{\"x\":1}", (await q.Jobs.GetAsync(A, job.Id))!.PayloadJson);
        Assert.Null(await q.Jobs.GetAsync(B, job.Id));
        Assert.Empty(await q.Jobs.ListAsync(B));
        Assert.Single(await q.Jobs.ListAsync(A, "import.validate"));
        Assert.Empty(await q.Jobs.ListAsync(A, "other.type"));
    }

    [Fact]
    public async Task ClaimingTakesTheOldestDueJobAndLeasesIt()
    {
        var q = New();
        var older = await Enqueue(q, A, T0);
        var newer = await Enqueue(q, A, T0.AddSeconds(1));

        var first = await q.Claimer.ClaimNextAsync("w1", T0.AddMinutes(1));
        Assert.Equal(older.Id, first!.Id);
        Assert.Equal(("running", "w1", 1), (first.Status, first.LockedBy, first.Attempts));
        Assert.Equal(T0.AddMinutes(1).AddMinutes(2), first.LockedUntil);

        Assert.Equal(newer.Id, (await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(1)))!.Id);
        Assert.Null(await q.Claimer.ClaimNextAsync("w3", T0.AddMinutes(1)));
    }

    [Fact]
    public async Task AJobIsNotClaimedBeforeItIsDue()
    {
        var q = New();
        await q.Jobs.EnqueueAsync(A, "later", "{}", "u1", T0.AddHours(1));

        Assert.Null(await q.Claimer.ClaimNextAsync("w1", T0));
        Assert.NotNull(await q.Claimer.ClaimNextAsync("w1", T0.AddHours(1)));
    }

    [Fact]
    public async Task ABusyTenantCannotStarveAQuietOne()
    {
        var q = New();
        var a = new List<Job>();
        for (var i = 0; i < 6; i++) a.Add(await Enqueue(q, A, T0.AddSeconds(i)));
        var b = await Enqueue(q, B, T0.AddMinutes(5));     // enqueued long after all of A's jobs

        var order = new List<string>();
        for (var i = 0; i < 3; i++) order.Add((await q.Claimer.ClaimNextAsync("w" + i, T0.AddMinutes(10)))!.TenantId);

        Assert.Equal(["jobs-tenant-a", "jobs-tenant-b", "jobs-tenant-a"], order);
        Assert.Equal("running", (await q.Jobs.GetAsync(B, b.Id))!.Status);
    }

    [Fact]
    public async Task NoTenantMayRunMoreJobsAtOnceThanItsCap()
    {
        var q = New();
        for (var i = 0; i < 4; i++) await Enqueue(q, A, T0.AddSeconds(i));
        var now = T0.AddMinutes(1);

        var one = await q.Claimer.ClaimNextAsync("w1", now);
        var two = await q.Claimer.ClaimNextAsync("w2", now);
        Assert.Null(await q.Claimer.ClaimNextAsync("w3", now));     // cap of 2 reached, work is waiting

        Assert.True(await q.Claimer.CompleteAsync(A, one!.Id, "w1", null, now));
        Assert.NotNull(await q.Claimer.ClaimNextAsync("w3", now));  // a slot opened
        Assert.NotNull(two);
    }

    [Fact]
    public async Task OnlyTheOwningWorkerCanCompleteAndOnlyOnce()
    {
        var q = New();
        var job = await Enqueue(q, A, T0);
        await q.Claimer.ClaimNextAsync("w1", T0);

        Assert.False(await q.Claimer.CompleteAsync(A, job.Id, "intruder", "{}", T0));
        Assert.False(await q.Claimer.CompleteAsync(B, job.Id, "w1", "{}", T0));         // wrong tenant
        Assert.True(await q.Claimer.CompleteAsync(A, job.Id, "w1", "{\"ok\":true}", T0.AddSeconds(5)));
        Assert.False(await q.Claimer.CompleteAsync(A, job.Id, "w1", "{}", T0.AddSeconds(6)));

        var done = await q.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("succeeded", "{\"ok\":true}", T0.AddSeconds(5)), (done!.Status, done.ResultJson, done.FinishedAt));
        Assert.Null(done.LockedBy);
    }

    [Fact]
    public async Task ARetriedJobWaitsOutItsBackoffThenFailsForGoodAfterTheLastAttempt()
    {
        var q = New();
        var job = await Enqueue(q, A, T0, maxAttempts: 2);

        await q.Claimer.ClaimNextAsync("w1", T0);
        Assert.True(await q.Claimer.FailAsync(A, job.Id, "w1", "boom", retry: true, T0));
        var queued = await q.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("queued", "boom", T0.AddSeconds(30)), (queued!.Status, queued.ErrorCode, queued.AvailableAt));

        Assert.Null(await q.Claimer.ClaimNextAsync("w1", T0.AddSeconds(29)));
        var second = await q.Claimer.ClaimNextAsync("w1", T0.AddSeconds(31));
        Assert.Equal(2, second!.Attempts);

        Assert.True(await q.Claimer.FailAsync(A, job.Id, "w1", "boom again", retry: true, T0.AddSeconds(32), "{\"detail\":1}"));
        var failed = await q.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("failed", "boom again", "{\"detail\":1}"), (failed!.Status, failed.ErrorCode, failed.ResultJson));
        Assert.Null(await q.Claimer.ClaimNextAsync("w1", T0.AddDays(1)));
    }

    [Fact]
    public async Task ANonRetryableFailureIsFinalImmediately()
    {
        var q = New();
        var job = await Enqueue(q, A, T0);
        await q.Claimer.ClaimNextAsync("w1", T0);

        await q.Claimer.FailAsync(A, job.Id, "w1", "bad_input", retry: false, T0);

        Assert.Equal("failed", (await q.Jobs.GetAsync(A, job.Id))!.Status);
    }

    [Fact]
    public async Task ADeadWorkersJobIsTakenOverAfterTheLeaseAndTheOldWorkerLosesOwnership()
    {
        var q = New();
        var job = await Enqueue(q, A, T0);
        await q.Claimer.ClaimNextAsync("w1", T0);

        Assert.Null(await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(1)));          // lease still valid
        var taken = await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(3));           // lease (2 min) expired
        Assert.Equal(("w2", 2), (taken!.LockedBy, taken.Attempts));

        Assert.False(await q.Claimer.HeartbeatAsync(A, job.Id, "w1", T0.AddMinutes(3)));
        Assert.False(await q.Claimer.CompleteAsync(A, job.Id, "w1", "{}", T0.AddMinutes(3)));
        Assert.True(await q.Claimer.CompleteAsync(A, job.Id, "w2", "{}", T0.AddMinutes(4)));
    }

    [Fact]
    public async Task AHeartbeatKeepsTheLeaseAlive()
    {
        var q = New();
        var job = await Enqueue(q, A, T0);
        await q.Claimer.ClaimNextAsync("w1", T0);

        Assert.True(await q.Claimer.HeartbeatAsync(A, job.Id, "w1", T0.AddMinutes(1.5)));      // lease now runs to 3:30
        Assert.Null(await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(3)));
        Assert.NotNull(await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(4)));
    }

    [Fact]
    public async Task ALostWorkerOnItsLastAttemptFailsTheJobInsteadOfLeavingItRunning()
    {
        var q = New();
        var job = await Enqueue(q, A, T0, maxAttempts: 1);
        await q.Claimer.ClaimNextAsync("w1", T0);

        Assert.Null(await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(5)));

        var failed = await q.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("failed", "lease_expired"), (failed!.Status, failed.ErrorCode));
    }

    [Fact]
    public async Task OnlyQueuedJobsCanBeCancelled()
    {
        var q = New();
        var waiting = await Enqueue(q, A, T0);
        var running = await Enqueue(q, A, T0.AddSeconds(1));
        await q.Claimer.ClaimNextAsync("w1", T0.AddMinutes(1));        // takes `waiting`
        Assert.Equal(waiting.Id, (await q.Jobs.ListAsync(A)).First(j => j.Status == "running").Id);

        Assert.False(await q.Jobs.CancelAsync(A, waiting.Id, T0));      // running
        Assert.False(await q.Jobs.CancelAsync(B, running.Id, T0));      // other tenant
        Assert.True(await q.Jobs.CancelAsync(A, running.Id, T0));
        Assert.Null(await q.Claimer.ClaimNextAsync("w2", T0.AddMinutes(1)));
        Assert.Equal("cancelled", (await q.Jobs.GetAsync(A, running.Id))!.Status);
    }

    [Fact]
    public async Task ProgressIsRecordedForTheOwnerOnly()
    {
        var q = New();
        var job = await Enqueue(q, A, T0);
        await q.Claimer.ClaimNextAsync("w1", T0);

        Assert.True(await q.Claimer.ReportProgressAsync(A, job.Id, "w1", 40, 100));
        Assert.False(await q.Claimer.ReportProgressAsync(A, job.Id, "w2", 99, 100));

        var read = await q.Jobs.GetAsync(A, job.Id);
        Assert.Equal((40, 100), (read!.ProgressDone, read.ProgressTotal));
    }

    [Fact]
    public async Task ManyWorkersClaimingAtOnceNeverTakeTheSameJobTwice()
    {
        var q = New(new JobQueueOptions(TimeSpan.FromMinutes(2), 1000, TimeSpan.FromSeconds(30)));
        for (var i = 0; i < 40; i++) await Enqueue(q, i % 2 == 0 ? A : B, T0.AddMilliseconds(i));

        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            while (await q.Claimer.ClaimNextAsync("w" + w, T0.AddMinutes(1)) is { } job) claimed.Add(job.Id);
        })));

        Assert.Equal(40, claimed.Count);
        Assert.Equal(40, claimed.Distinct().Count());
    }
}
