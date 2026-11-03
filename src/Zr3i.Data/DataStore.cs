using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Builds repositories for a database. Only a composition root (Web, Worker, tests) creates one;
/// the Kernel and the modules receive repositories and never see a driver type.
/// </summary>
public sealed class DataStore(IMongoDatabase database)
{
    public TenantRepository<T> Tenant<T>(string collection) where T : class, ITenantEntity =>
        new(database.GetCollection<T>(collection));

    public AppendOnlyRepository<T> AppendOnly<T>(string collection) where T : class, ITenantEntity =>
        new(database.GetCollection<T>(collection));
}
