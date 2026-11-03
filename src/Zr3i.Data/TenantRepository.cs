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

    public async Task<bool> DeleteAsync(TenantScope scope, string id, CancellationToken ct = default)
    {
        var result = await _rows.Collection.DeleteOneAsync(_rows.ById(scope, id), ct).ConfigureAwait(false);
        return result.DeletedCount == 1;
    }
}
