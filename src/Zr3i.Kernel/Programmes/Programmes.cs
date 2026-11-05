using System.Globalization;
using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;

namespace Zr3i.Kernel.Programmes;

/// <summary>A named group of the organisation's own parcels that is checked together, for example a financing tranche.</summary>
public sealed class Tranche : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;

    /// <summary>yyyy-MM-dd, or null.</summary>
    public string? ScheduledDate { get; set; }
    public List<string> ParcelIds { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>A date range (for example a hail storm) over a group of the organisation's own parcels, for which the satellite record is compared before and after.</summary>
public sealed class EventWindow : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<string> ParcelIds { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed record ParcelSelection(IReadOnlyList<string>? FarmIds, IReadOnlyList<string>? ParcelIds, IReadOnlyList<string>? ExternalIds);

/// <summary>Turns "these farms, these parcels, these external ids" into the organisation's own parcel ids. Anything that is not the organisation's own is refused.</summary>
public static class ParcelSelector
{
    public const int Max = 5000;

    /// <exception cref="ValidationFailedException">unknown_farm, unknown_parcel, no_parcels, too_many_parcels.</exception>
    public static async Task<List<string>> ResolveAsync(KernelStore store, TenantScope scope, ParcelSelection s, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        var farms = (s.FarmIds ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct().ToList();
        if (farms.Count > 0)
        {
            var found = (await store.Farms.FindAsync(scope, f => farms.Contains(f.Id), 1000, ct)).Select(f => f.Id).ToHashSet();
            if (farms.Any(f => !found.Contains(f))) throw new ValidationFailedException("unknown_farm");
            string? cursor = null;
            do
            {
                var page = await store.Parcels.FindPageAsync(scope, p => farms.Contains(p.FarmId), cursor, 1000, ct);
                foreach (var p in page.Items) ids.Add(p.Id);
                cursor = page.NextCursor;
            } while (cursor is not null && ids.Count <= Max);
        }

        var direct = (s.ParcelIds ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
        foreach (var chunk in direct.Chunk(500))
        {
            var c = chunk.ToList();
            var found = (await store.Parcels.FindAsync(scope, p => c.Contains(p.Id), 1000, ct)).Select(p => p.Id).ToHashSet();
            if (c.Any(p => !found.Contains(p))) throw new ValidationFailedException("unknown_parcel");
            foreach (var p in c) ids.Add(p);
        }

        var external = (s.ExternalIds ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct().ToList();
        foreach (var chunk in external.Chunk(500))
        {
            var c = chunk.ToList();
            var found = await store.Parcels.FindAsync(scope, p => p.ExternalId != null && c.Contains(p.ExternalId), 1000, ct);
            if (c.Any(e => found.All(p => p.ExternalId != e))) throw new ValidationFailedException("unknown_parcel");
            foreach (var p in found) ids.Add(p.Id);
        }

        if (ids.Count == 0) throw new ValidationFailedException("no_parcels");
        if (ids.Count > Max) throw new ValidationFailedException("too_many_parcels");
        return [.. ids.Order(StringComparer.Ordinal)];
    }
}

public sealed record TrancheRequest(string? Name, string? Season, string? ScheduledDate, ParcelSelection Parcels);

public sealed record TrancheCheck(Tranche Tranche, IReadOnlyDictionary<string, int> Verdicts, IReadOnlyList<ParcelListItem> NeedsALook, int NeedsALookTotal, string Accuracy);

/// <summary>Tranches: create, list, and check (the verdicts of the tranche's parcels, and the ones that need a person's look). Reads only the organisation's own data.</summary>
public sealed class TrancheService(KernelStore store, ParcelListService parcels, AuditService audit, TimeProvider clock)
{
    public const int MaxListed = 500;

    /// <exception cref="ValidationFailedException">no_name, invalid_season, invalid_date, duplicate_name, and the selection codes.</exception>
    public async Task<Tranche> CreateAsync(TenantScope scope, string actorUserId, TrancheRequest r, CancellationToken ct = default)
    {
        var name = r.Name?.Trim() ?? string.Empty;
        var season = r.Season?.Trim() ?? string.Empty;
        var errors = new List<string>();
        if (name.Length is 0 or > 60) errors.Add("no_name");
        if (!FarmRules.IsValidSeason(season)) errors.Add("invalid_season");
        if (!string.IsNullOrWhiteSpace(r.ScheduledDate) && !Dates.IsValid(r.ScheduledDate)) errors.Add("invalid_date");
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        if (await store.Tranches.CountAsync(scope, t => t.Season == season && t.Name == name, ct) > 0) throw new ValidationFailedException("duplicate_name");

        var ids = await ParcelSelector.ResolveAsync(store, scope, r.Parcels, ct);
        var tranche = new Tranche
        {
            Name = name, Season = season, ScheduledDate = string.IsNullOrWhiteSpace(r.ScheduledDate) ? null : r.ScheduledDate.Trim(), ParcelIds = ids,
            CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = actorUserId,
        };
        await store.Tranches.InsertAsync(scope, tranche, ct);
        await audit.RecordAsync(scope, actorUserId, "tranche.created", "tranche", tranche.Id, purpose: $"{name} · {ids.Count} parcels", ct: ct);
        return tranche;
    }

    public async Task<IReadOnlyList<Tranche>> ListAsync(TenantScope scope, CancellationToken ct = default) =>
        [.. (await store.Tranches.FindAsync(scope, _ => true, 1000, ct)).OrderBy(t => t.Season, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal)];

    /// <exception cref="NotFoundException">tranche.</exception>
    public async Task<TrancheCheck> CheckAsync(TenantScope scope, string id, string? season, CancellationToken ct = default)
    {
        var tranche = await store.Tranches.GetAsync(scope, id, ct) ?? throw new NotFoundException("tranche", id);
        var items = await parcels.ForParcelsAsync(scope, tranche.ParcelIds, string.IsNullOrWhiteSpace(season) ? tranche.Season : season, ct);

        var verdicts = Verdicts.All.Where(v => v != Verdicts.None).ToDictionary(v => v, v => items.Count(i => i.Finding?.Status == v));
        verdicts[Verdicts.None] = items.Count(i => i.Finding is null);
        var look = items.Where(i => i.Finding?.Status != "MEASURED").OrderBy(i => i.Label, StringComparer.Ordinal).ToList();
        return new TrancheCheck(tranche, verdicts, [.. look.Take(MaxListed)], look.Count, "NOT_ESTABLISHED");
    }
}

public sealed record EventWindowRequest(string? Name, string? Season, string? From, string? To, ParcelSelection Parcels);

public sealed record SceneRef(string SceneId, DateTime SensingTime, double Ndvi);

public sealed record EventParcelEvidence(string ParcelId, string Label, string FarmName, SceneRef? Before, SceneRef? After, double? NdviChange, string Status, string? Reason);

public sealed record EventEvidence(EventWindow Window, int CoveredParcels, int CoveredFarms, int ParcelsWithEvidence, IReadOnlyList<EventParcelEvidence> Parcels);

/// <summary>
/// Event windows and their evidence: for each parcel, the nearest usable scene before the window and the nearest after it, with their NDVI and the
/// difference. The platform states the numbers; whether that amounts to damage is for a person to decide, so there is no verdict here.
/// </summary>
public sealed class EventWindowService(KernelStore store, ParcelListService parcels, AuditService audit, TimeProvider clock)
{
    /// <exception cref="ValidationFailedException">no_name, invalid_season, invalid_date, invalid_range, duplicate_name, and the selection codes.</exception>
    public async Task<EventWindow> CreateAsync(TenantScope scope, string actorUserId, EventWindowRequest r, CancellationToken ct = default)
    {
        var name = r.Name?.Trim() ?? string.Empty;
        var season = r.Season?.Trim() ?? string.Empty;
        var errors = new List<string>();
        if (name.Length is 0 or > 60) errors.Add("no_name");
        if (!FarmRules.IsValidSeason(season)) errors.Add("invalid_season");
        if (!Dates.IsValid(r.From) || !Dates.IsValid(r.To)) errors.Add("invalid_date");
        else if (string.CompareOrdinal(r.From, r.To) > 0) errors.Add("invalid_range");
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        if (await store.EventWindows.CountAsync(scope, w => w.Season == season && w.Name == name, ct) > 0) throw new ValidationFailedException("duplicate_name");

        var ids = await ParcelSelector.ResolveAsync(store, scope, r.Parcels, ct);
        var window = new EventWindow { Name = name, Season = season, From = r.From!.Trim(), To = r.To!.Trim(), ParcelIds = ids, CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = actorUserId };
        await store.EventWindows.InsertAsync(scope, window, ct);
        await audit.RecordAsync(scope, actorUserId, "event_window.created", "event_window", window.Id, purpose: $"{name} · {window.From} to {window.To} · {ids.Count} parcels", ct: ct);
        return window;
    }

    public async Task<IReadOnlyList<EventWindow>> ListAsync(TenantScope scope, CancellationToken ct = default) =>
        [.. (await store.EventWindows.FindAsync(scope, _ => true, 1000, ct)).OrderBy(w => w.From, StringComparer.Ordinal).ThenBy(w => w.Name, StringComparer.Ordinal)];

    /// <exception cref="NotFoundException">event_window.</exception>
    public async Task<EventEvidence> EvidenceAsync(TenantScope scope, string id, CancellationToken ct = default)
    {
        var window = await store.EventWindows.GetAsync(scope, id, ct) ?? throw new NotFoundException("event_window", id);
        var from = Dates.Parse(window.From);
        var afterStart = Dates.Parse(window.To).AddDays(1);                                   // the whole last day of the window belongs to it

        var items = await parcels.ForParcelsAsync(scope, window.ParcelIds, window.Season, ct);
        var byParcel = new Dictionary<string, (SceneRef? Before, SceneRef? After)>();
        foreach (var chunk in window.ParcelIds.Chunk(200))
        {
            var ids = chunk.ToList();
            string? cursor = null;
            do
            {
                var page = await store.ParcelObservations.FindPageAsync(scope, o => ids.Contains(o.ParcelId) && o.Readable && o.NdviMedian != null, cursor, 1000, ct);
                foreach (var o in page.Items)
                {
                    var scene = new SceneRef(o.SceneId, o.SensingTime, o.NdviMedian!.Value);
                    byParcel.TryGetValue(o.ParcelId, out var cur);
                    if (o.SensingTime < from && (cur.Before is null || o.SensingTime > cur.Before.SensingTime)) cur.Before = scene;
                    if (o.SensingTime >= afterStart && (cur.After is null || o.SensingTime < cur.After.SensingTime)) cur.After = scene;
                    byParcel[o.ParcelId] = cur;
                }

                cursor = page.NextCursor;
            } while (cursor is not null);
        }

        var rows = items.OrderBy(i => i.Label, StringComparer.Ordinal).Select(i =>
        {
            byParcel.TryGetValue(i.Id, out var s);
            var both = s.Before is not null && s.After is not null;
            return new EventParcelEvidence(i.Id, i.Label, i.FarmName, s.Before, s.After, both ? s.After!.Ndvi - s.Before!.Ndvi : null, both ? "compared" : "not_assessable",
                both ? null : s.Before is null ? "no_clear_scene_before" : "no_clear_scene_after");
        }).ToList();

        return new EventEvidence(window, rows.Count, items.Select(i => i.FarmId).Distinct().Count(), rows.Count(r => r.Status == "compared"), rows);
    }
}

internal static class Dates
{
    public static bool IsValid(string? s) => s is not null && DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static DateTime Parse(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
