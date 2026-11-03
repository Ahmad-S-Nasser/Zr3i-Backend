using MongoDB.Driver;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>
/// A throwaway database on a local MongoDB (no auth, ADR 0001). The server comes from ZR3I_TEST_MONGO,
/// default mongodb://localhost:27017. The database is dropped when the fixture is disposed.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private MongoClient? _client;

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var uri = Environment.GetEnvironmentVariable("ZR3I_TEST_MONGO") ?? "mongodb://localhost:27017";
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        _client = new MongoClient(settings);
        var name = "zr3i_test_" + Guid.NewGuid().ToString("N");
        Database = _client.GetDatabase(name);
        await Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
            await _client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }
}

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "mongo";
}
