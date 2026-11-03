using MongoDB.Driver;
using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class JobRunnerTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("runner-tenant-a");

    private sealed record Payload(string Name, int Count);

    private sealed class Handler(string type, Func<JobContext, CancellationToken, Task<JobOutcome>> run) : IJobHandler
    {
        public string Type => type;

        public Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct) => run(context, ct);
    }

    private (DataStore Data, JobRunner Runner, IMongoDatabase Db) New(
        Handler[] handlers, TimeSpan? lease = null, TimeSpan? heartbeat = null, string worker = "w1", TimeProvider? clock = null, IMongoDatabase? db = null)
    {
        db ??= mongo.Database.Client.GetDatabase("zr3i_test_runner_" + Guid.NewGuid().ToString("N"));
        var data = new DataStore(db);
        var claimer = data.CreateJobClaimer(new JobQueueOptions(lease ?? TimeSpan.FromMinutes(2), 2, TimeSpan.Zero));
        return (data, new JobRunner(claimer, handlers, clock ?? TimeProvider.System, worker, heartbeat), db);
    }

    private static Task<Job> Enqueue(DataStore data, string type, object? payload = null, int maxAttempts = 3) =>
        data.Jobs.EnqueueAsync(A, type, JobJson.Serialize(payload ?? new { }), "u1", DateTime.UtcNow.AddSeconds(-1), maxAttempts);

    [Fact]
    public async Task ASuccessfulHandlerGetsItsPayloadAndScopeAndItsResultIsStored()
    {
        TenantScope? seen = null;
        var (data, runner, _) = New([new Handler("echo", async (ctx, ct) =>
        {
            seen = ctx.Scope;
            var p = ctx.Payload<Payload>();
            await ctx.ReportProgressAsync(p.Count, p.Count, ct);
            return JobOutcome.Success(JobJson.Serialize(new { greeting = "hello " + p.Name }));
        })]);
        var job = await Enqueue(data, "echo", new Payload("Ahmad", 7));

        Assert.True(await runner.RunOnceAsync());
        Assert.False(await runner.RunOnceAsync());

        var done = await data.Jobs.GetAsync(A, job.Id);
        Assert.Equal(A, seen);
        Assert.Equal(("succeeded", 7, 7, "{\"greeting\":\"hello Ahmad\"}"), (done!.Status, done.ProgressDone, done.ProgressTotal, done.ResultJson));
    }

    [Fact]
    public async Task AHandlerThatThrowsIsRetriedAndSucceedsOnTheNextAttempt()
    {
        var calls = 0;
        var (data, runner, _) = New([new Handler("flaky", (_, _) =>
            ++calls == 1 ? throw new InvalidOperationException("first try fails") : Task.FromResult(JobOutcome.Success()))]);
        var job = await Enqueue(data, "flaky");

        await runner.RunOnceAsync();
        var afterFirst = await data.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("queued", "handler_exception:InvalidOperationException", 1), (afterFirst!.Status, afterFirst.ErrorCode, afterFirst.Attempts));

        await runner.RunOnceAsync();
        Assert.Equal(("succeeded", 2), ((await data.Jobs.GetAsync(A, job.Id))!.Status, (await data.Jobs.GetAsync(A, job.Id))!.Attempts));
    }

    [Fact]
    public async Task AFinalFailureIsNotRetriedAndKeepsItsDetails()
    {
        var calls = 0;
        var (data, runner, _) = New([new Handler("bad", (_, _) =>
        {
            calls++;
            return Task.FromResult(JobOutcome.Failure("bad_file", "{\"rows\":3}"));
        })]);
        var job = await Enqueue(data, "bad");

        await runner.RunUntilIdleAsync();

        var failed = await data.Jobs.GetAsync(A, job.Id);
        Assert.Equal(1, calls);
        Assert.Equal(("failed", "bad_file", "{\"rows\":3}"), (failed!.Status, failed.ErrorCode, failed.ResultJson));
    }

    [Fact]
    public async Task ATypeNobodyHandlesFailsAtOnce()
    {
        var (data, runner, _) = New([]);
        var job = await Enqueue(data, "mystery");

        await runner.RunOnceAsync();

        Assert.Equal(("failed", "unknown_job_type"), ((await data.Jobs.GetAsync(A, job.Id))!.Status, (await data.Jobs.GetAsync(A, job.Id))!.ErrorCode));
    }

    [Fact]
    public async Task ARepeatedlyThrowingHandlerStopsAfterItsAttempts()
    {
        var (data, runner, _) = New([new Handler("always", (_, _) => throw new InvalidOperationException())]);
        var job = await Enqueue(data, "always", maxAttempts: 3);

        Assert.Equal(3, await runner.RunUntilIdleAsync());

        var failed = await data.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("failed", 3), (failed!.Status, failed.Attempts));
    }

    [Fact]
    public async Task AHeartbeatKeepsALongJobFromBeingTakenOver()
    {
        var (data, runner, db) = New(
            [new Handler("slow", async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(3.5), ct); return JobOutcome.Success(); })],
            lease: TimeSpan.FromSeconds(1.5), heartbeat: TimeSpan.FromMilliseconds(300));
        var job = await Enqueue(data, "slow");

        var running = runner.RunOnceAsync();
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // A second worker, well past the first lease's original expiry, finds nothing to take.
        var other = data.CreateJobClaimer(new JobQueueOptions(TimeSpan.FromSeconds(1.5), 2, TimeSpan.Zero));
        Assert.Null(await other.ClaimNextAsync("w2", DateTime.UtcNow));

        await running;
        var done = await data.Jobs.GetAsync(A, job.Id);
        Assert.Equal(("succeeded", 1), (done!.Status, done.Attempts));
        _ = db;
    }

    [Fact]
    public async Task AHandlerIsCancelledAndNothingIsWrittenWhenItsJobIsTakenAway()
    {
        var sawCancellation = false;
        var (data, runner, db) = New(
            [new Handler("wait", async (_, ct) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { sawCancellation = true; throw; }
                return JobOutcome.Success("{\"should\":\"not be stored\"}");
            })],
            lease: TimeSpan.FromSeconds(30), heartbeat: TimeSpan.FromMilliseconds(200));
        var job = await Enqueue(data, "wait");

        var running = runner.RunOnceAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        await db.GetCollection<Job>(Collections.Jobs).UpdateOneAsync(j => j.Id == job.Id, Builders<Job>.Update.Set(j => j.LockedBy, "someone-else"));

        Assert.True(await running);
        var after = await data.Jobs.GetAsync(A, job.Id);
        Assert.True(sawCancellation);
        Assert.Equal(("running", "someone-else", null), (after!.Status, after.LockedBy, after.ResultJson));
    }
}
