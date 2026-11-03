using System.Linq.Expressions;
using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>
/// For audit, consent, verification, evidence and indicator results (CLAUDE.md rule 5).
/// Insert and read only: there is no update or delete to call. Database-level enforcement
/// (an insert-and-find role) is added when Mongo auth is switched on (ADR 0001).
/// </summary>
public sealed class AppendOnlyRepository<T>(IMongoCollection<T> collection) where T : class, ITenantEntity
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
}
