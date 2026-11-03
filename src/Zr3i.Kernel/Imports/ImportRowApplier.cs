using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;

namespace Zr3i.Kernel.Imports;

/// <summary>
/// Applies one checked row: finds or creates the portfolio, farm and parcel by external id, amends the season claim, and adds
/// a boundary when the shape is new. A row that changes nothing writes nothing, which is what makes re-importing a file safe.
/// Two rules: a blank cell never erases an existing value, and an existing parcel never moves to another farm.
/// One instance serves one commit; it caches the portfolios and farms it has seen.
/// </summary>
public sealed class ImportRowApplier(KernelStore store, BoundaryService boundaries, AuditService audit, TimeProvider clock, TenantScope scope, string actorUserId, ImportBatch batch)
{
    private readonly Dictionary<string, Portfolio> _portfolios = [];
    private readonly Dictionary<string, Farm> _farms = [];

    /// <exception cref="ValidationFailedException">The row cannot be applied (for example "parcel_belongs_to_other_farm"); nothing from it is half-written that matters.</exception>
    public async Task<string> ApplyAsync(ParsedRow row, CancellationToken ct = default)
    {
        var changed = false;

        // Refuse a parcel that already belongs to another farm before anything is written, so a refused row leaves nothing behind.
        var existing = (await store.Parcels.FindAsync(scope, p => p.ExternalId == row.ParcelExternalId, 1, ct)).FirstOrDefault();
        if (existing is not null)
        {
            var owner = _farms.Values.FirstOrDefault(f => f.Id == existing.FarmId) ?? await store.Farms.GetAsync(scope, existing.FarmId, ct);
            if (owner?.ExternalId != row.FarmExternalId) throw new ValidationFailedException("parcel_belongs_to_other_farm");
        }

        Portfolio? portfolio = null;
        if (row.PortfolioExternalId is not null)
        {
            var ensured = await EnsurePortfolioAsync(row, ct);
            portfolio = ensured.Value;
            changed |= ensured.Changed;
        }

        var (farm, farmChanged) = await EnsureFarmAsync(row, portfolio, ct);
        changed |= farmChanged;

        var (parcel, parcelCreated, parcelChanged) = await EnsureParcelAsync(row, farm, existing, ct);
        changed |= parcelChanged;

        if (row.Season is not null) changed |= await EnsureSeasonAsync(row, parcel, ct);
        if (row.BoundaryCheck is not null) changed |= await EnsureBoundaryAsync(row, parcel, ct);

        return parcelCreated ? ImportActions.Created : changed ? ImportActions.Updated : ImportActions.Unchanged;
    }

    private async Task<(Portfolio? Value, bool Changed)> EnsurePortfolioAsync(ParsedRow row, CancellationToken ct)
    {
        var externalId = row.PortfolioExternalId!;
        if (!_portfolios.TryGetValue(externalId, out var portfolio))
        {
            portfolio = (await store.Portfolios.FindAsync(scope, p => p.ExternalId == externalId, 1, ct)).FirstOrDefault();
            if (portfolio is not null) _portfolios[externalId] = portfolio;
        }

        if (portfolio is null)
        {
            portfolio = new Portfolio { Name = row.PortfolioName ?? externalId, ExternalId = externalId, CustomFields = new(row.PortfolioFields), CreatedAt = Now() };
            await store.Portfolios.InsertAsync(scope, portfolio, ct);
            _portfolios[externalId] = portfolio;
            return (portfolio, true);
        }

        var changed = false;
        if (row.PortfolioName is not null && row.PortfolioName != portfolio.Name) { portfolio.Name = row.PortfolioName; changed = true; }
        changed |= Merge(portfolio.CustomFields, row.PortfolioFields);
        if (changed) await store.Portfolios.ReplaceAsync(scope, portfolio, ct);
        return (portfolio, changed);
    }

    private async Task<(Farm Farm, bool Changed)> EnsureFarmAsync(ParsedRow row, Portfolio? portfolio, CancellationToken ct)
    {
        if (!_farms.TryGetValue(row.FarmExternalId, out var farm))
        {
            farm = (await store.Farms.FindAsync(scope, f => f.ExternalId == row.FarmExternalId, 1, ct)).FirstOrDefault();
            if (farm is not null) _farms[row.FarmExternalId] = farm;
        }

        if (farm is null)
        {
            farm = new Farm
            {
                Name = row.FarmName,
                PortfolioId = portfolio?.Id,
                Governorate = row.Governorate,
                Markaz = row.Markaz,
                Village = row.Village,
                CadastralRef = row.CadastralRef,
                ExternalId = row.FarmExternalId,
                CustomFields = new(row.FarmFields),
                CreatedAt = Now(),
            };
            await store.Farms.InsertAsync(scope, farm, ct);
            _farms[row.FarmExternalId] = farm;
            return (farm, true);
        }

        var changed = false;
        changed |= Set(farm.Name, row.FarmName, v => farm.Name = v!);
        changed |= SetIfGiven(farm.Governorate, row.Governorate, v => farm.Governorate = v);
        changed |= SetIfGiven(farm.Markaz, row.Markaz, v => farm.Markaz = v);
        changed |= SetIfGiven(farm.Village, row.Village, v => farm.Village = v);
        changed |= SetIfGiven(farm.CadastralRef, row.CadastralRef, v => farm.CadastralRef = v);
        changed |= SetIfGiven(farm.PortfolioId, portfolio?.Id, v => farm.PortfolioId = v);
        changed |= Merge(farm.CustomFields, row.FarmFields);
        if (changed) await store.Farms.ReplaceAsync(scope, farm, ct);
        return (farm, changed);
    }

    private async Task<(Parcel Parcel, bool Created, bool Changed)> EnsureParcelAsync(ParsedRow row, Farm farm, Parcel? parcel, CancellationToken ct)
    {
        if (parcel is null)
        {
            parcel = new Parcel { FarmId = farm.Id, Label = row.ParcelLabel, ExternalId = row.ParcelExternalId, CustomFields = new(row.ParcelFields), CreatedAt = Now() };
            await store.Parcels.InsertAsync(scope, parcel, ct);
            return (parcel, true, true);
        }

        var changed = Set(parcel.Label, row.ParcelLabel, v => parcel.Label = v!);
        changed |= Merge(parcel.CustomFields, row.ParcelFields);
        if (changed) await store.Parcels.ReplaceAsync(scope, parcel, ct);
        return (parcel, false, changed);
    }

    private async Task<bool> EnsureSeasonAsync(ParsedRow row, Parcel parcel, CancellationToken ct)
    {
        var season = (await store.ParcelSeasons.FindAsync(scope, s => s.ParcelId == parcel.Id && s.Season == row.Season, 1, ct)).FirstOrDefault();
        if (season is null)
        {
            season = new ParcelSeason
            {
                ParcelId = parcel.Id,
                FarmId = parcel.FarmId,
                Season = row.Season!,
                DeclaredCrop = row.DeclaredCrop,
                DeclaredAreaFeddan = row.DeclaredAreaFeddan,
                DeclaredSowingDate = row.DeclaredSowingDate,
                DeclaredSource = row.DeclaredSource ?? $"import:{batch.FileName}",
                DeclaredAt = Now(),
                CustomFields = new(row.SeasonFields),
            };
            await store.ParcelSeasons.InsertAsync(scope, season, ct);
            await audit.RecordAsync(scope, actorUserId, "parcel_season.declared", "parcel_season", season.Id, ct: ct);
            return true;
        }

        var changed = SetIfGiven(season.DeclaredCrop, row.DeclaredCrop, v => season.DeclaredCrop = v);
        if (row.DeclaredAreaFeddan is { } area && area != season.DeclaredAreaFeddan) { season.DeclaredAreaFeddan = area; changed = true; }
        changed |= SetIfGiven(season.DeclaredSowingDate, row.DeclaredSowingDate, v => season.DeclaredSowingDate = v);
        changed |= SetIfGiven(season.DeclaredSource, row.DeclaredSource, v => season.DeclaredSource = v);
        changed |= Merge(season.CustomFields, row.SeasonFields);
        if (!changed) return false;

        season.DeclaredAt = Now();
        await store.ParcelSeasons.ReplaceAsync(scope, season, ct);
        await audit.RecordAsync(scope, actorUserId, "parcel_season.declared", "parcel_season", season.Id, ct: ct);
        return true;
    }

    private async Task<bool> EnsureBoundaryAsync(ParsedRow row, Parcel parcel, CancellationToken ct)
    {
        var check = row.BoundaryCheck!;
        var hash = BoundaryHash.Of(check.Rings);
        if (parcel.CurrentBoundaryId is { } currentId
            && await store.Boundaries.GetAsync(scope, currentId, ct) is { } current
            && current.GeometrySha256 == hash)
            return false;   // the same shape again

        var provenance = new BoundaryProvenance(row.BoundarySource ?? BoundarySources.Import, row.CapturedBy ?? $"import:{batch.FileName}", row.CapturedAt ?? batch.CreatedAt, row.AccuracyM);
        await boundaries.AddAsync(scope, actorUserId, parcel.Id, check, provenance, ct);
        return true;
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

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
}
