using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// The only place a tenant filter is built. Repositories compose this; none of them build a filter themselves.
/// </summary>
internal sealed class TenantCollection<T>(IMongoCollection<T> collection) where T : class, ITenantEntity
{
    private const string IdField = "_id";
    private const string TenantField = "TenantId";
    internal const int MaxPage = 1000;

    public IMongoCollection<T> Collection { get; } = collection;

    public FilterDefinition<T> ForTenant(TenantScope scope) =>
        Builders<T>.Filter.Eq(TenantField, scope.TenantId);

    public FilterDefinition<T> ForTenant(TenantScope scope, Expression<Func<T, bool>> predicate) =>
        Builders<T>.Filter.And(ForTenant(scope), Builders<T>.Filter.Where(predicate));

    public FilterDefinition<T> ById(TenantScope scope, string id) =>
        Builders<T>.Filter.And(ForTenant(scope), Builders<T>.Filter.Eq(IdField, id));

    public async Task InsertAsync(TenantScope scope, T entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!string.IsNullOrEmpty(entity.TenantId) && entity.TenantId != scope.TenantId)
            throw new TenantMismatchException(entity.TenantId, scope.TenantId);

        entity.TenantId = scope.TenantId;
        if (string.IsNullOrEmpty(entity.Id)) entity.Id = ObjectId.GenerateNewId().ToString();
        await Collection.InsertOneAsync(entity, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync(TenantScope scope, string id, CancellationToken ct) =>
        await Collection.Find(ById(scope, id)).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<T>> FindAsync(TenantScope scope, Expression<Func<T, bool>> predicate, int limit, CancellationToken ct)
    {
        CheckLimit(limit);
        return await Collection.Find(ForTenant(scope, predicate))
            .Sort(Builders<T>.Sort.Ascending(IdField)).Limit(limit).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Page<T>> FindPageAsync(TenantScope scope, Expression<Func<T, bool>> predicate, string? cursor, int pageSize, CancellationToken ct)
    {
        CheckLimit(pageSize);
        var filter = ForTenant(scope, predicate);
        if (!string.IsNullOrEmpty(cursor))
            filter = Builders<T>.Filter.And(filter, Builders<T>.Filter.Gt(IdField, cursor));

        var rows = await Collection.Find(filter).Sort(Builders<T>.Sort.Ascending(IdField))
            .Limit(pageSize + 1).ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count <= pageSize) return new Page<T>(rows, null);
        var items = rows.Take(pageSize).ToList();
        return new Page<T>(items, items[^1].Id);
    }

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > MaxPage) throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be 1 to {MaxPage}.");
    }
}
