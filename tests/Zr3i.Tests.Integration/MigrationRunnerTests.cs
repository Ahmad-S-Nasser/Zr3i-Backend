using MongoDB.Driver;
using Zr3i.Data;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class MigrationRunnerTests(MongoFixture mongo)
{
    private sealed class CountingMigration(string id, Action onRun) : IMigration
    {
        public string Id => id;

        public Task UpAsync(IMongoDatabase database, CancellationToken ct)
        {
            onRun();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingMigration(string id) : IMigration
    {
        public string Id => id;
        public Task UpAsync(IMongoDatabase database, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private IMongoDatabase FreshDb() => mongo.Database.Client.GetDatabase("zr3i_test_mig_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunsPendingMigrationsInIdOrderOnce()
    {
        var db = FreshDb();
        var order = new List<string>();
        var runner = new MigrationRunner(db,
        [
            new CountingMigration("0002", () => order.Add("0002")),
            new CountingMigration("0001", () => order.Add("0001")),
        ]);

        var first = await runner.RunAsync();
        var second = await runner.RunAsync();

        Assert.Equal(["0001", "0002"], order);
        Assert.Equal(["0001", "0002"], first);
        Assert.Empty(second);
        await mongo.Database.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public async Task AFailedMigrationIsNotMarkedAppliedAndBlocksTheRest()
    {
        var db = FreshDb();
        var ranAfter = false;
        var runner = new MigrationRunner(db, [new FailingMigration("0001"), new CountingMigration("0002", () => ranAfter = true)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync());

        Assert.False(ranAfter);
        // The failed one is left as 'started' so a person looks at it; it is never silently re-run.
        await Assert.ThrowsAsync<MigrationInterruptedException>(() => runner.RunAsync());
        await mongo.Database.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void RejectsDuplicateMigrationIds()
    {
        var db = FreshDb();
        Assert.Throws<ArgumentException>(() => new MigrationRunner(db,
            [new CountingMigration("0001", () => { }), new CountingMigration("0001", () => { })]));
    }
}
