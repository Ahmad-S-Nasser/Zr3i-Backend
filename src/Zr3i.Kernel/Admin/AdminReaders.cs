using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel.Admin;

public sealed record MemberView(string UserId, string Email, string Language, bool Active, bool AuthenticatorEnrolled, DateTime? LockedUntil, IReadOnlyList<RoleRef> Roles, int FarmScope, int PortfolioScope);

public sealed record InvitationView(string Email, string Status, DateTime ExpiresAt, IReadOnlyList<RoleRef> Roles);

public sealed record MemberDirectory(IReadOnlyList<MemberView> Members, IReadOnlyList<InvitationView> Invitations, IReadOnlyList<RoleRef> Roles);

/// <summary>Who belongs to one organisation, in which roles, and which invitations are still open. Never returns a password hash or an authenticator secret.</summary>
public sealed class MemberDirectoryService(KernelStore store, TimeProvider clock)
{
    public async Task<MemberDirectory> ListAsync(TenantScope scope, CancellationToken ct = default)
    {
        var memberships = await store.Memberships.FindAsync(scope, _ => true, 1000, ct);
        var userIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = (await store.Users.FindAsync(PlatformTenant.Scope, u => userIds.Contains(u.Id), 1000, ct)).ToDictionary(u => u.Id);
        var roles = await store.Roles.FindAsync(scope, _ => true, 1000, ct);
        var byId = roles.ToDictionary(r => r.Id, r => new RoleRef(r.Key, r.Name));

        var members = memberships.Where(m => users.ContainsKey(m.UserId)).Select(m =>
        {
            var u = users[m.UserId];
            return new MemberView(u.Id, u.Email, u.Language, u.IsActive && m.IsActive, !string.IsNullOrEmpty(u.TotpSecret), u.LockedUntil,
                [.. m.RoleIds.Where(byId.ContainsKey).Select(id => byId[id]).OrderBy(r => r.Key, StringComparer.Ordinal)], m.FarmIds.Count, m.PortfolioIds.Count);
        }).OrderBy(m => m.Email, StringComparer.Ordinal).ToList();

        var now = clock.GetUtcNow().UtcDateTime;
        var invitations = (await store.Invitations.FindAsync(scope, i => i.AcceptedAt == null, 1000, ct))
            .OrderBy(i => i.Email, StringComparer.Ordinal)
            .Select(i => new InvitationView(i.Email, i.ExpiresAt > now ? "pending" : "expired", i.ExpiresAt,
                [.. i.RoleIds.Where(byId.ContainsKey).Select(id => byId[id])]))
            .ToList();

        return new MemberDirectory(members, invitations, [.. byId.Values.OrderBy(r => r.Key, StringComparer.Ordinal)]);
    }
}

public sealed record AuditEntryView(string Id, DateTime At, string? ActorUserId, string? ActorEmail, string Action, string ObjectType, string ObjectId, string Outcome, string? Purpose);

public sealed record AuditPage(IReadOnlyList<AuditEntryView> Items, string? NextCursor);

/// <summary>
/// The organisation's audit log, newest first. A window (default 30 days) bounds what is read; the cursor is a position in that window.
/// The log itself is append-only: this only reads it.
/// </summary>
public sealed class AuditReader(KernelStore store, TimeProvider clock)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;
    public const int MaxScanned = 5000;

    /// <exception cref="ValidationFailedException">invalid_page_size, invalid_cursor, invalid_date.</exception>
    public async Task<AuditPage> ListAsync(TenantScope scope, string? cursor, int? pageSize, string? action, string? from, string? to, CancellationToken ct = default)
    {
        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize) throw new ValidationFailedException("invalid_page_size");
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && (!int.TryParse(cursor, out offset) || offset < 0)) throw new ValidationFailedException("invalid_cursor");

        var now = clock.GetUtcNow().UtcDateTime;
        var start = ParseDate(from) ?? now.AddDays(-30);
        var end = ParseDate(to)?.AddDays(1) ?? now.AddMinutes(1);
        var needle = string.IsNullOrWhiteSpace(action) ? null : action.Trim();

        var events = new List<AuditEvent>();
        string? next = null;
        do
        {
            var page = await store.Audit.FindPageAsync(scope, e => e.At >= start && e.At < end && (needle == null || e.Action.Contains(needle)), next, 1000, ct);
            events.AddRange(page.Items);
            next = page.NextCursor;
        } while (next is not null && events.Count < MaxScanned);

        var ordered = events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();
        var slice = ordered.Skip(offset).Take(size).ToList();

        var actorIds = slice.Where(e => e.ActorUserId != null).Select(e => e.ActorUserId!).Distinct().ToList();
        var emails = actorIds.Count == 0 ? [] : (await store.Users.FindAsync(PlatformTenant.Scope, u => actorIds.Contains(u.Id), 1000, ct)).ToDictionary(u => u.Id, u => u.Email);

        var items = slice.Select(e => new AuditEntryView(e.Id, e.At, e.ActorUserId, e.ActorUserId is { } a ? emails.GetValueOrDefault(a) : null, e.Action, e.ObjectType, e.ObjectId, e.Outcome, e.Purpose)).ToList();
        return new AuditPage(items, offset + size < ordered.Count ? (offset + size).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : throw new ValidationFailedException("invalid_date");
    }
}

public sealed record TenantView(string Id, string Name, string AccountType, IReadOnlyList<string> Modules, DateTime CreatedAt, int Members, int Farms, int Parcels);

/// <summary>
/// Every customer organisation, for the provider. Organisations are stored per tenant, so they are found through the users who belong to them;
/// an organisation nobody has joined yet does not appear until its first user accepts an invitation.
/// </summary>
public sealed class TenantDirectoryService(KernelStore store)
{
    public async Task<IReadOnlyList<TenantView>> ListAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await store.Users.FindPageAsync(PlatformTenant.Scope, _ => true, cursor, 1000, ct);
            foreach (var u in page.Items) foreach (var id in u.OrganisationIds) if (id != PlatformTenant.Id) ids.Add(id);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var result = new List<TenantView>();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var scope = TenantScope.ForTenant(id);
            if (await store.Organisations.GetAsync(scope, id, ct) is not { } org) continue;
            result.Add(new TenantView(org.Id, org.Name, org.AccountType, org.Modules, org.CreatedAt,
                (int)await store.Memberships.CountAsync(scope, _ => true, ct), (int)await store.Farms.CountAsync(scope, _ => true, ct), (int)await store.Parcels.CountAsync(scope, _ => true, ct)));
        }

        return [.. result.OrderBy(t => t.Name, StringComparer.Ordinal)];
    }
}

public sealed record RegionCount(string Name, int Farms);

public sealed record PortfolioSummary(int Farms, int Parcels, double AreaFeddan, IReadOnlyList<RegionCount> Regions, string? Governorate);

/// <summary>Headline figures for the organisation, optionally for one region. Area is the sum of the current boundaries' measured area.</summary>
public sealed class PortfolioService(KernelStore store)
{
    public async Task<PortfolioSummary> SummaryAsync(TenantScope scope, string? governorate, CancellationToken ct = default)
    {
        var region = string.IsNullOrWhiteSpace(governorate) ? null : governorate.Trim();
        var farms = await AllAsync((c, n) => store.Farms.FindPageAsync(scope, _ => true, c, n, ct));
        var regions = farms.Where(f => !string.IsNullOrWhiteSpace(f.Governorate)).GroupBy(f => f.Governorate!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RegionCount(g.Key, g.Count())).ToList();

        var inRegion = farms.Where(f => region == null || f.Governorate == region).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var parcels = await AllAsync((c, n) => store.Parcels.FindPageAsync(scope, _ => true, c, n, ct));
        var mine = parcels.Where(p => inRegion.Contains(p.FarmId)).ToList();

        var boundaryIds = mine.Where(p => p.CurrentBoundaryId != null).Select(p => p.CurrentBoundaryId!).ToList();
        double area = 0;
        foreach (var chunk in boundaryIds.Chunk(500))
        {
            var ids = chunk.ToList();
            area += (await store.Boundaries.FindAsync(scope, b => ids.Contains(b.Id), 1000, ct)).Sum(b => b.AreaFeddan);
        }

        return new PortfolioSummary(inRegion.Count, mine.Count, area, regions, region);
    }

    private static async Task<List<T>> AllAsync<T>(Func<string?, int, Task<Page<T>>> next)
    {
        var all = new List<T>();
        string? cursor = null;
        do
        {
            var page = await next(cursor, 1000);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }
}
