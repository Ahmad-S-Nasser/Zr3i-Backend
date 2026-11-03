using System.Linq.Expressions;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// Read and write access to a tenant-owned collection. Every call takes a <see cref="TenantScope"/>;
/// no method can reach another tenant's rows. Callers never see a driver type.
/// </summary>
public sealed class TenantRepository<T>(IMongoCollection<T> collection) where T : class, ITenantEntity
{
    private readonly TenantCollection<T> _rows = new(collection);

    public Task InsertAsync(TenantScope scope, T entity, CancellationToken ct = default) =>
        _rows.InsertAsync(scope, entity, ct);

    /// <summary>Inserts many rows in one round trip, all stamped with the scope's tenant. For bulk staging.</summary>
    public Task InsertManyAsync(TenantScope scope, IReadOnlyList<T> entities, CancellationToken ct = default) =>
        _rows.InsertManyAsync(scope, entities, ct);

    public Task<T?> GetAsync(TenantScope scope, string id, CancellationToken ct = default) =>
        _rows.GetAsync(scope, id, ct);

    public Task<IReadOnlyList<T>> FindAsync(TenantScope scope, Expression<Func<T, bool>> predicate, int limit = 1000, CancellationToken ct = default) =>
        _rows.FindAsync(scope, predicate, limit, ct);

    public Task<Page<T>> FindPageAsync(TenantScope scope, Expression<Func<T, bool>> predicate, string? cursor, int pageSize, CancellationToken ct = default) =>
        _rows.FindPageAsync(scope, predicate, cursor, pageSize, ct);

    public async Task<long> CountAsync(TenantScope scope, Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        await _rows.Collection.CountDocumentsAsync(_rows.ForTenant(scope, predicate), cancellationToken: ct).ConfigureAwait(false);

    /// <summary>Replaces the row with the same id inside the tenant. The owner tenant is always the scope's. False if not found.</summary>
    public async Task<bool> ReplaceAsync(TenantScope scope, T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.TenantId = scope.TenantId;
        try
        {
            var result = await _rows.Collection.ReplaceOneAsync(_rows.ById(scope, entity.Id), entity, cancellationToken: ct).ConfigureAwait(false);
            return result.MatchedCount == 1;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateKeyException(e);
        }
    }

    /// <summary>Replaces many rows (matched by id inside the tenant) in one round trip. Returns how many existed. For bulk updates.</summary>
    public async Task<long> ReplaceManyAsync(TenantScope scope, IReadOnlyList<T> entities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0) return 0;

        var models = new List<WriteModel<T>>(entities.Count);
        foreach (var entity in entities)
        {
            entity.TenantId = scope.TenantId;
            models.Add(new ReplaceOneModel<T>(_rows.ById(scope, entity.Id), entity));
        }

        try
        {
            var result = await _rows.Collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
            return result.MatchedCount;
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Count > 0 && e.WriteErrors.All(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            throw new DuplicateKeyException(e);
        }
    }

    /// <summary>Deletes every row of the tenant that matches. Returns how many were removed.</summary>
    public async Task<long> DeleteWhereAsync(TenantScope scope, Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        (await _rows.Collection.DeleteManyAsync(_rows.ForTenant(scope, predicate), ct).ConfigureAwait(false)).DeletedCount;

    public async Task<bool> DeleteAsync(TenantScope scope, string id, CancellationToken ct = default)
    {
        var result = await _rows.Collection.DeleteOneAsync(_rows.ById(scope, id), ct).ConfigureAwait(false);
        return result.DeletedCount == 1;
    }
}
