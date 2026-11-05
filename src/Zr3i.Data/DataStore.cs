using MongoDB.Bson;
using MongoDB.Driver;
using Zr3i.Data.Jobs;

namespace Zr3i.Data;

/// <summary>
/// Builds repositories for a database. Only a composition root (Web, Worker, tests) creates one;
/// the Kernel and the modules receive repositories and never see a driver type.
/// </summary>
public sealed class DataStore(IMongoDatabase database)
{
    /// <summary>For hosts that must not reference the driver: connect from a connection string.</summary>
    public static DataStore Connect(string connectionString, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        return new DataStore(new MongoClient(settings).GetDatabase(databaseName));
    }

    /// <summary>The tenant-scoped side of the job queue (enqueue, look at, cancel).</summary>
    public JobQueue Jobs { get; } = new(database.GetCollection<Job>(Collections.Jobs));

    /// <summary>The worker side of the queue. Only the worker host creates one.</summary>
    public JobClaimer CreateJobClaimer(JobQueueOptions? options = null) => new(database, options ?? JobQueueOptions.Default);

    public TenantRepository<T> Tenant<T>(string collection) where T : class, ITenantEntity =>
        new(database.GetCollection<T>(collection));

    public AppendOnlyRepository<T> AppendOnly<T>(string collection) where T : class, ITenantEntity =>
        new(database.GetCollection<T>(collection));

    /// <summary>Applies the Kernel migrations (indexes). Modules add theirs as they appear.</summary>
    public Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default) =>
        new MigrationRunner(database, [new KernelIndexesMigration(), new FarmIndexesMigration(), new JobIndexesMigration(), new ImportIndexesMigration(), new AccountIndexesMigration(), new MethodIndexesMigration(), new SceneIndexesMigration(), new CropConfigIndexesMigration(), new FindingIndexesMigration(), new IndicatorIndexesMigration(), new ReferenceIndexesMigration(), new ProgrammeIndexesMigration(), new ReportIndexesMigration()]).RunAsync(ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            return false;
        }
    }
}
