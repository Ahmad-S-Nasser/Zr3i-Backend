using System.Globalization;
using System.Text;
using Zr3i.Data;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Imports;

namespace Zr3i.Kernel.Admin;

/// <summary>What one organisation holds and did. Parcels and area are what it holds now; everything else is counted inside the period.</summary>
public sealed record UsageNumbers(
    int Parcels, double AreaFeddan, int Readings, int UsableReadings, int Scenes, int Pictures, long PictureBytes, int Reports, int Downloads,
    int ImportBatches, int ImportedRows, int Grants, int Withdrawals, int SharedReads)
{
    public static UsageNumbers Sum(IEnumerable<UsageNumbers> all) => all.Aggregate(new UsageNumbers(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), (a, b) => new UsageNumbers(
        a.Parcels + b.Parcels, a.AreaFeddan + b.AreaFeddan, a.Readings + b.Readings, a.UsableReadings + b.UsableReadings, a.Scenes + b.Scenes, a.Pictures + b.Pictures,
        a.PictureBytes + b.PictureBytes, a.Reports + b.Reports, a.Downloads + b.Downloads, a.ImportBatches + b.ImportBatches, a.ImportedRows + b.ImportedRows,
        a.Grants + b.Grants, a.Withdrawals + b.Withdrawals, a.SharedReads + b.SharedReads));
}

public sealed record UsageRow(string Id, string Name, string AccountType, UsageNumbers Numbers);

public sealed record PlatformUsage(DateTime From, DateTime To, IReadOnlyList<UsageRow> Organisations, UsageNumbers Totals);

/// <summary>
/// Usage across organisations for the provider's revenue share (ADR 0020): counts only, never a record. It visits each organisation with that
/// organisation's own scope, the same way the tenant directory does, so no query ever spans organisations. Reads of shared data are counted
/// for the receiving organisation and only when a member of it did the reading (a provider's oversight view is not usage).
/// </summary>
public sealed class PlatformUsageService(KernelStore store, TenantDirectoryService directory, PortfolioService portfolio)
{
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(400);
    private const int Page = 1000;
    private const int MaxRowsPerCount = 50_000;

    /// <exception cref="ValidationFailedException">invalid_period.</exception>
    public async Task<PlatformUsage> UsageAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (to <= from || to - from > MaxPeriod) throw new ValidationFailedException("invalid_period");
        var rows = new List<UsageRow>();
        foreach (var t in await directory.ListAsync(ct))
            rows.Add(new UsageRow(t.Id, t.Name, t.AccountType, await CountAsync(TenantScope.ForTenant(t.Id), from, to, ct)));
        return new PlatformUsage(from, to, rows, UsageNumbers.Sum(rows.Select(r => r.Numbers)));
    }

    private async Task<UsageNumbers> CountAsync(TenantScope scope, DateTime from, DateTime to, CancellationToken ct)
    {
        var holdings = await portfolio.SummaryAsync(scope, null, ct);

        var readings = (int)await store.ParcelObservations.CountAsync(scope, o => o.IngestedAt >= from && o.IngestedAt < to, ct);
        var usable = (int)await store.ParcelObservations.CountAsync(scope, o => o.IngestedAt >= from && o.IngestedAt < to && o.Readable, ct);
        var scenes = new HashSet<string>(StringComparer.Ordinal);
        await ForEachPageAsync((c, n) => store.ParcelObservations.FindPageAsync(scope, o => o.IngestedAt >= from && o.IngestedAt < to, c, n, ct), o => scenes.Add(o.SceneId));

        int pictures = 0;
        long pictureBytes = 0;
        await ForEachPageAsync((c, n) => store.ParcelChips.FindPageAsync(scope, p => p.CreatedAt >= from && p.CreatedAt < to, c, n, ct), p => { pictures++; pictureBytes += p.Bytes; });

        var reports = (int)await store.FrozenReports.CountAsync(scope, r => r.CreatedAt >= from && r.CreatedAt < to, ct);
        // Downloads count only when a member of the organisation did them: a provider looking in through oversight is not the organisation's usage.
        var members = (await store.Memberships.FindAsync(scope, _ => true, Page, ct)).Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        var downloads = 0;
        await ForEachPageAsync((c, n) => store.Audit.FindPageAsync(scope, e => e.Action == "report.downloaded" && e.At >= from && e.At < to, c, n, ct),
            e => { if (e.ActorUserId is not null && members.Contains(e.ActorUserId)) downloads++; });

        var batches = await store.ImportBatches.FindAsync(scope, b => b.CreatedAt >= from && b.CreatedAt < to, Page, ct);
        var imported = batches.Where(b => b.Status == ImportStatus.Committed).Sum(b => b.Created + b.Updated);

        var grants = (int)await store.ConsentEvents.CountAsync(scope, e => e.OwnerTenantId == scope.TenantId && e.Event == ConsentEventTypes.Granted && e.At >= from && e.At < to, ct);
        var withdrawals = (int)await store.ConsentEvents.CountAsync(scope, e => e.OwnerTenantId == scope.TenantId && e.Event == ConsentEventTypes.Withdrawn && e.At >= from && e.At < to, ct);

        return new UsageNumbers(holdings.Parcels, holdings.AreaFeddan, readings, usable, scenes.Count, pictures, pictureBytes, reports, downloads, batches.Count, imported, grants, withdrawals,
            await SharedReadsAsync(scope, from, to, ct));
    }

    /// <summary>Reads of data shared TO this organisation (grants where it is the receiver), by its own members.</summary>
    private async Task<int> SharedReadsAsync(TenantScope scope, DateTime from, DateTime to, CancellationToken ct)
    {
        var grantIds = (await store.ConsentEvents.FindAsync(scope, e => e.GranteeTenantId == scope.TenantId && e.Event == ConsentEventTypes.Granted, Page, ct)).Select(e => e.GrantId).ToHashSet(StringComparer.Ordinal);
        if (grantIds.Count == 0) return 0;
        var members = (await store.Memberships.FindAsync(scope, _ => true, Page, ct)).Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        await ForEachPageAsync((c, n) => store.Audit.FindPageAsync(scope, e => e.Action == "consent.read" && e.At >= from && e.At < to, c, n, ct),
            e => { if (e.ActorUserId is not null && members.Contains(e.ActorUserId) && grantIds.Contains(e.ObjectId)) count++; });
        return count;
    }

    private static async Task ForEachPageAsync<T>(Func<string?, int, Task<Page<T>>> next, Action<T> each)
    {
        string? cursor = null;
        var seen = 0;
        do
        {
            var page = await next(cursor, Page);
            foreach (var item in page.Items) each(item);
            seen += page.Items.Count;
            cursor = page.NextCursor;
        } while (cursor is not null && seen < MaxRowsPerCount);
    }

    /// <summary>The usage as a CSV for billing. Code headers, one row per organisation and a total row. A cell that starts like a formula gets a leading apostrophe.</summary>
    public static string ToCsv(PlatformUsage usage)
    {
        var sb = new StringBuilder("id,name,account_type,parcels,area_feddan,readings,usable_readings,scenes,pictures,picture_bytes,reports,downloads,import_batches,imported_rows,grants,withdrawals,shared_reads\r\n");
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        void Line(string id, string name, string type, UsageNumbers n) => sb.Append(string.Join(',', new[]
        {
            Cell(id), Cell(name), Cell(type), N(n.Parcels), N(n.AreaFeddan), N(n.Readings), N(n.UsableReadings), N(n.Scenes), N(n.Pictures), N(n.PictureBytes), N(n.Reports), N(n.Downloads),
            N(n.ImportBatches), N(n.ImportedRows), N(n.Grants), N(n.Withdrawals), N(n.SharedReads),
        })).Append("\r\n");
        foreach (var r in usage.Organisations) Line(r.Id, r.Name, r.AccountType, r.Numbers);
        Line("", "TOTAL", "", usage.Totals);
        return sb.ToString();
    }

    public static string Cell(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
