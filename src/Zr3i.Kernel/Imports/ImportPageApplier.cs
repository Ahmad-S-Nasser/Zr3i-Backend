using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;

namespace Zr3i.Kernel.Imports;

/// <summary>The result for one row: what happened to it, or why it was refused.</summary>
public sealed record RowOutcome(string? Action, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Action is not null;

    public static RowOutcome Done(string action) => new(action, []);

    public static RowOutcome Refused(params string[] errors) => new(null, errors);
}

/// <summary>
/// Applies a page of checked rows: finds or creates the portfolios, farms and parcels by external id, amends season claims, and
/// adds a boundary when the shape is new. A page is handled in a handful of queries: everything the rows touch is loaded at once,
/// the changes are worked out in memory, and they are written with bulk inserts and replaces. A row that changes nothing writes
/// nothing, which is what makes re-importing a file safe. Rules: a blank cell never erases a stored value, an existing parcel
/// never moves to another farm, and a refused row changes nothing.
/// One instance serves one commit and keeps the portfolios and farms it has seen.
/// </summary>
public sealed class ImportPageApplier(KernelStore store, AuditService audit, TimeProvider clock, TenantScope scope, string actorUserId, ImportBatch batch)
{
    private const int LoadPage = 1000;

    private readonly Dictionary<string, Portfolio> _portfolios = [];
    private readonly Dictionary<string, Farm> _farms = [];
    private readonly Dictionary<string, Farm> _farmsById = [];

    /// <summary>Forget what was cached. Called after a failed bulk write, so the next attempt reads the database as it now is.</summary>
    public void Reset()
    {
        _portfolios.Clear();
        _farms.Clear();
        _farmsById.Clear();
    }

    /// <returns>One outcome per row, in order.</returns>
    /// <exception cref="DuplicateKeyException">A bulk write hit a unique key (someone else imported the same ids at the same moment). Nothing is assumed about what was written; the caller retries row by row.</exception>
    public async Task<IReadOnlyList<RowOutcome>> ApplyAsync(IReadOnlyList<ParsedRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return [];
        var now = clock.GetUtcNow().UtcDateTime;
        var w = new Writes();

        // 1. Load everything this page touches.
        var parcelExternalIds = rows.Select(r => r.ParcelExternalId).Distinct().ToList();
        var parcels = (await AllAsync(c => store.Parcels.FindPageAsync(scope, p => parcelExternalIds.Contains(p.ExternalId!), c, LoadPage, ct)))
            .ToDictionary(p => p.ExternalId!);
        await LoadFarmsAsync(rows, parcels.Values, ct);
        await LoadPortfoliosAsync(rows, ct);

        var parcelIds = parcels.Values.Select(p => p.Id).ToList();
        var seasons = parcelIds.Count == 0
            ? []
            : (await AllAsync(c => store.ParcelSeasons.FindPageAsync(scope, s => parcelIds.Contains(s.ParcelId), c, LoadPage, ct))).ToDictionary(s => SeasonKey(s.ParcelId, s.Season));
        var currentIds = parcels.Values.Select(p => p.CurrentBoundaryId).Where(id => id is not null).Select(id => id!).ToList();
        var tips = currentIds.Count == 0
            ? []
            : (await AllAsync(c => store.Boundaries.FindPageAsync(scope, b => currentIds.Contains(b.Id), c, LoadPage, ct))).ToDictionary(b => b.ParcelId);

        // 2. Work out the changes in memory.
        var outcomes = new List<RowOutcome>(rows.Count);
        var createdParcels = new HashSet<string>();
        foreach (var row in rows)
        {
            try
            {
                outcomes.Add(RowOutcome.Done(ApplyRow(row, parcels, seasons, tips, createdParcels, w, now)));
            }
            catch (ValidationFailedException e)
            {
                outcomes.Add(RowOutcome.Refused([.. e.Codes]));
            }
        }

        // 3. Write, parents before children.
        await store.Portfolios.InsertManyAsync(scope, w.NewPortfolios, ct);
        await store.Farms.InsertManyAsync(scope, w.NewFarms, ct);
        await store.Boundaries.InsertManyAsync(scope, w.NewBoundaries, ct);
        await store.ParcelSeasons.InsertManyAsync(scope, w.NewSeasons, ct);
        await store.Parcels.InsertManyAsync(scope, w.NewParcels, ct);
        await store.Portfolios.ReplaceManyAsync(scope, [.. w.ChangedPortfolios], ct);
        await store.Farms.ReplaceManyAsync(scope, [.. w.ChangedFarms], ct);
        await store.ParcelSeasons.ReplaceManyAsync(scope, [.. w.ChangedSeasons], ct);
        await store.Parcels.ReplaceManyAsync(scope, [.. w.ChangedParcels], ct);
        await audit.RecordManyAsync(scope, w.Audits, ct);
        return outcomes;
    }

    private string ApplyRow(
        ParsedRow row,
        Dictionary<string, Parcel> parcels,
        Dictionary<string, ParcelSeason> seasons,
        Dictionary<string, BoundaryVersion> tips,
        HashSet<string> createdParcels,
        Writes w,
        DateTime now)
    {
        parcels.TryGetValue(row.ParcelExternalId, out var parcel);

        // A parcel never moves farm. Checked before anything is touched, so a refused row leaves nothing behind.
        if (parcel is not null && (!_farmsById.TryGetValue(parcel.FarmId, out var owner) || owner.ExternalId != row.FarmExternalId))
            throw new ValidationFailedException("parcel_belongs_to_other_farm");

        // Prepare the boundary first: it can refuse the row.
        var parcelId = parcel?.Id ?? NewId.Create();
        tips.TryGetValue(parcelId, out var tip);
        BoundaryVersion? boundary = null;
        if (row.BoundaryCheck is { } check && (tip is null || tip.GeometrySha256 != BoundaryHash.Of(check.Rings)))
        {
            var provenance = new BoundaryProvenance(row.BoundarySource ?? BoundarySources.Import, row.CapturedBy ?? $"import:{batch.FileName}", row.CapturedAt ?? batch.CreatedAt, row.AccuracyM);
            boundary = BoundaryService.CreateVersion(parcelId, check, provenance, tip, now, actorUserId);
        }

        var changed = false;
        var parcelCreatedHere = false;

        var portfolio = row.PortfolioExternalId is null ? null : EnsurePortfolio(row, w, now, ref changed);
        var farm = EnsureFarm(row, portfolio, w, now, ref changed);

        if (parcel is null)
        {
            parcel = new Parcel { Id = parcelId, FarmId = farm.Id, Label = row.ParcelLabel, ExternalId = row.ParcelExternalId, CustomFields = new(row.ParcelFields), CreatedAt = now };
            parcels[row.ParcelExternalId] = parcel;
            createdParcels.Add(parcel.Id);
            w.NewParcels.Add(parcel);
            parcelCreatedHere = true;
        }
        else if (!createdParcels.Contains(parcel.Id))
        {
            var parcelChanged = Set(parcel.Label, row.ParcelLabel, v => parcel.Label = v!);
            parcelChanged |= Merge(parcel.CustomFields, row.ParcelFields);
            if (parcelChanged) { w.ChangedParcels.Add(parcel); changed = true; }
        }

        if (row.Season is not null) changed |= EnsureSeason(row, parcel, seasons, w, now);

        if (boundary is not null)
        {
            w.NewBoundaries.Add(boundary);
            w.Audits.Add(audit.Create(actorUserId, "boundary.added", "boundary", boundary.Id));
            tips[parcel.Id] = boundary;
            parcel.CurrentBoundaryId = boundary.Id;
            if (!createdParcels.Contains(parcel.Id)) w.ChangedParcels.Add(parcel);
            changed = true;
        }

        // "Created" only on the row that created the parcel; a later row for it (another season) is an update.
        return parcelCreatedHere ? ImportActions.Created : changed ? ImportActions.Updated : ImportActions.Unchanged;
    }

    private Portfolio EnsurePortfolio(ParsedRow row, Writes w, DateTime now, ref bool changed)
    {
        var externalId = row.PortfolioExternalId!;
        if (!_portfolios.TryGetValue(externalId, out var portfolio))
        {
            portfolio = new Portfolio { Id = NewId.Create(), Name = row.PortfolioName ?? externalId, ExternalId = externalId, CustomFields = new(row.PortfolioFields), CreatedAt = now };
            _portfolios[externalId] = portfolio;
            w.NewPortfolios.Add(portfolio);
            changed = true;
            return portfolio;
        }

        if (w.NewPortfolios.Contains(portfolio)) return portfolio;   // created earlier in this page; its first row already set it
        var c = row.PortfolioName is not null && Set(portfolio.Name, row.PortfolioName, v => portfolio.Name = v!);
        c |= Merge(portfolio.CustomFields, row.PortfolioFields);
        if (c) { w.ChangedPortfolios.Add(portfolio); changed = true; }
        return portfolio;
    }

    private Farm EnsureFarm(ParsedRow row, Portfolio? portfolio, Writes w, DateTime now, ref bool changed)
    {
        if (!_farms.TryGetValue(row.FarmExternalId, out var farm))
        {
            farm = new Farm
            {
                Id = NewId.Create(),
                Name = row.FarmName,
                PortfolioId = portfolio?.Id,
                Governorate = row.Governorate,
                Markaz = row.Markaz,
                Village = row.Village,
                CadastralRef = row.CadastralRef,
                ExternalId = row.FarmExternalId,
                CustomFields = new(row.FarmFields),
                CreatedAt = now,
            };
            _farms[row.FarmExternalId] = farm;
            _farmsById[farm.Id] = farm;
            w.NewFarms.Add(farm);
            changed = true;
            return farm;
        }

        if (w.NewFarms.Contains(farm)) return farm;
        var c = Set(farm.Name, row.FarmName, v => farm.Name = v!);
        c |= SetIfGiven(farm.Governorate, row.Governorate, v => farm.Governorate = v);
        c |= SetIfGiven(farm.Markaz, row.Markaz, v => farm.Markaz = v);
        c |= SetIfGiven(farm.Village, row.Village, v => farm.Village = v);
        c |= SetIfGiven(farm.CadastralRef, row.CadastralRef, v => farm.CadastralRef = v);
        c |= SetIfGiven(farm.PortfolioId, portfolio?.Id, v => farm.PortfolioId = v);
        c |= Merge(farm.CustomFields, row.FarmFields);
        if (c) { w.ChangedFarms.Add(farm); changed = true; }
        return farm;
    }

    private bool EnsureSeason(ParsedRow row, Parcel parcel, Dictionary<string, ParcelSeason> seasons, Writes w, DateTime now)
    {
        var key = SeasonKey(parcel.Id, row.Season!);
        if (!seasons.TryGetValue(key, out var season))
        {
            season = new ParcelSeason
            {
                Id = NewId.Create(),
                ParcelId = parcel.Id,
                FarmId = parcel.FarmId,
                Season = row.Season!,
                DeclaredCrop = row.DeclaredCrop,
                DeclaredAreaFeddan = row.DeclaredAreaFeddan,
                DeclaredSowingDate = row.DeclaredSowingDate,
                DeclaredSource = row.DeclaredSource ?? $"import:{batch.FileName}",
                DeclaredAt = now,
                CustomFields = new(row.SeasonFields),
            };
            seasons[key] = season;
            w.NewSeasons.Add(season);
            w.Audits.Add(audit.Create(actorUserId, "parcel_season.declared", "parcel_season", season.Id));
            return true;
        }

        var changed = SetIfGiven(season.DeclaredCrop, row.DeclaredCrop, v => season.DeclaredCrop = v);
        if (row.DeclaredAreaFeddan is { } area && area != season.DeclaredAreaFeddan) { season.DeclaredAreaFeddan = area; changed = true; }
        changed |= SetIfGiven(season.DeclaredSowingDate, row.DeclaredSowingDate, v => season.DeclaredSowingDate = v);
        changed |= SetIfGiven(season.DeclaredSource, row.DeclaredSource, v => season.DeclaredSource = v);
        changed |= Merge(season.CustomFields, row.SeasonFields);
        if (!changed) return false;

        season.DeclaredAt = now;
        if (!w.NewSeasons.Contains(season)) w.ChangedSeasons.Add(season);
        w.Audits.Add(audit.Create(actorUserId, "parcel_season.declared", "parcel_season", season.Id));
        return true;
    }

    private async Task LoadFarmsAsync(IReadOnlyList<ParsedRow> rows, IEnumerable<Parcel> existingParcels, CancellationToken ct)
    {
        var externalIds = rows.Select(r => r.FarmExternalId).Distinct().Where(e => !_farms.ContainsKey(e)).ToList();
        var ids = existingParcels.Select(p => p.FarmId).Distinct().Where(id => !_farmsById.ContainsKey(id)).ToList();
        var loaded = new List<Farm>();
        if (externalIds.Count > 0) loaded.AddRange(await AllAsync(c => store.Farms.FindPageAsync(scope, f => externalIds.Contains(f.ExternalId!), c, LoadPage, ct)));
        if (ids.Count > 0) loaded.AddRange(await AllAsync(c => store.Farms.FindPageAsync(scope, f => ids.Contains(f.Id), c, LoadPage, ct)));
        foreach (var farm in loaded.DistinctBy(f => f.Id))
        {
            _farmsById[farm.Id] = farm;
            if (farm.ExternalId is not null) _farms[farm.ExternalId] = farm;
        }
    }

    private async Task LoadPortfoliosAsync(IReadOnlyList<ParsedRow> rows, CancellationToken ct)
    {
        var externalIds = rows.Select(r => r.PortfolioExternalId).Where(e => e is not null && !_portfolios.ContainsKey(e)).Select(e => e!).Distinct().ToList();
        if (externalIds.Count == 0) return;
        foreach (var portfolio in await AllAsync(c => store.Portfolios.FindPageAsync(scope, p => externalIds.Contains(p.ExternalId!), c, LoadPage, ct)))
            _portfolios[portfolio.ExternalId!] = portfolio;
    }

    private static async Task<List<T>> AllAsync<T>(Func<string?, Task<Page<T>>> page)
    {
        var all = new List<T>();
        string? cursor = null;
        do
        {
            var p = await page(cursor);
            all.AddRange(p.Items);
            cursor = p.NextCursor;
        } while (cursor is not null);

        return all;
    }

    private static string SeasonKey(string parcelId, string season) => parcelId + "|" + season;

    private static bool Set(string? current, string? value, Action<string?> assign)
    {
        if (current == value) return false;
        assign(value);
        return true;
    }

    /// <summary>A blank cell leaves the stored value alone; the import never erases.</summary>
    private static bool SetIfGiven(string? current, string? value, Action<string?> assign) => value is not null && Set(current, value, assign);

    private static bool Merge(Dictionary<string, string> target, Dictionary<string, string> incoming)
    {
        var changed = false;
        foreach (var (key, value) in incoming)
        {
            if (target.TryGetValue(key, out var existing) && existing == value) continue;
            target[key] = value;
            changed = true;
        }

        return changed;
    }

    /// <summary>Everything a page will write, collected before any of it is.</summary>
    private sealed class Writes
    {
        public List<Portfolio> NewPortfolios { get; } = [];
        public HashSet<Portfolio> ChangedPortfolios { get; } = [];
        public List<Farm> NewFarms { get; } = [];
        public HashSet<Farm> ChangedFarms { get; } = [];
        public List<Parcel> NewParcels { get; } = [];
        public HashSet<Parcel> ChangedParcels { get; } = [];
        public List<ParcelSeason> NewSeasons { get; } = [];
        public HashSet<ParcelSeason> ChangedSeasons { get; } = [];
        public List<BoundaryVersion> NewBoundaries { get; } = [];
        public List<AuditEvent> Audits { get; } = [];
    }
}
