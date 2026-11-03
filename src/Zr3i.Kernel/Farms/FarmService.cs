using System.Globalization;
using System.Text.RegularExpressions;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Farms;

public sealed record NewPortfolio(string Name, string? ExternalId = null, IReadOnlyDictionary<string, string?>? CustomFields = null);

public sealed record NewFarm(
    string Name,
    string? PortfolioId = null,
    string? Governorate = null,
    string? Markaz = null,
    string? Village = null,
    string? CadastralRef = null,
    string? ExternalId = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

public sealed record NewParcel(string FarmId, string Label, string? ExternalId = null, IReadOnlyDictionary<string, string?>? CustomFields = null);

/// <param name="Season">A lower-case key such as "2026-27-winter".</param>
/// <param name="DeclaredSowingDate">yyyy-MM-dd.</param>
/// <param name="DeclaredSource">Where the claim came from, for example "customer contract".</param>
public sealed record NewParcelSeason(
    string ParcelId,
    string Season,
    string? DeclaredCrop,
    double? DeclaredAreaFeddan,
    string? DeclaredSowingDate,
    string? DeclaredSource,
    string? ExternalId = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

/// <summary>
/// Creates portfolios, farms, parcels and parcel-seasons inside one tenant. Parents must exist in the same tenant,
/// external ids are unique per tenant, and custom fields are checked against the tenant's own definitions.
/// </summary>
public sealed partial class FarmService(KernelStore store, CustomFieldService customFields, AuditService audit, TimeProvider clock)
{
    private const int MaxName = 200;
    private const int MaxExternalId = 100;
    private const double MaxDeclaredFeddan = BoundaryRules.MaxAreaM2 / GeoConstants.SquareMetresPerFeddan;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex SeasonShape();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial Regex CropShape();

    public async Task<Portfolio> CreatePortfolioAsync(TenantScope scope, string actorUserId, NewPortfolio request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var name = Required(request.Name, MaxName, "name_required", errors);
        var externalId = ExternalId(request.ExternalId, errors);
        Throw(errors);

        var portfolio = new Portfolio
        {
            Name = name,
            ExternalId = externalId,
            CustomFields = await customFields.ValidateAsync(scope, CustomFieldEntities.Portfolio, request.CustomFields, ct),
            CreatedAt = Now(),
        };
        await InsertAsync(() => store.Portfolios.InsertAsync(scope, portfolio, ct));
        return portfolio;
    }

    public async Task<Farm> CreateFarmAsync(TenantScope scope, string actorUserId, NewFarm request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var name = Required(request.Name, MaxName, "name_required", errors);
        var externalId = ExternalId(request.ExternalId, errors);
        Throw(errors);

        if (!string.IsNullOrWhiteSpace(request.PortfolioId) && await store.Portfolios.GetAsync(scope, request.PortfolioId, ct) is null)
            throw new NotFoundException("portfolio", request.PortfolioId);

        var farm = new Farm
        {
            Name = name,
            PortfolioId = Blank(request.PortfolioId),
            Governorate = Blank(request.Governorate),
            Markaz = Blank(request.Markaz),
            Village = Blank(request.Village),
            CadastralRef = Blank(request.CadastralRef),
            ExternalId = externalId,
            CustomFields = await customFields.ValidateAsync(scope, CustomFieldEntities.Farm, request.CustomFields, ct),
            CreatedAt = Now(),
        };
        await InsertAsync(() => store.Farms.InsertAsync(scope, farm, ct));
        return farm;
    }

    public async Task<Parcel> CreateParcelAsync(TenantScope scope, string actorUserId, NewParcel request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var label = Required(request.Label, MaxName, "label_required", errors);
        var externalId = ExternalId(request.ExternalId, errors);
        Throw(errors);

        if (await store.Farms.GetAsync(scope, request.FarmId ?? string.Empty, ct) is null) throw new NotFoundException("farm", request.FarmId ?? string.Empty);

        var parcel = new Parcel
        {
            FarmId = request.FarmId!,
            Label = label,
            ExternalId = externalId,
            CustomFields = await customFields.ValidateAsync(scope, CustomFieldEntities.Parcel, request.CustomFields, ct),
            CreatedAt = Now(),
        };
        await InsertAsync(() => store.Parcels.InsertAsync(scope, parcel, ct));
        return parcel;
    }

    /// <summary>
    /// Records (or amends) the customer's claim for one parcel in one season. One row per parcel and season; every
    /// declaration is audited, so an amended claim leaves a trail.
    /// </summary>
    public async Task<ParcelSeason> DeclareSeasonAsync(TenantScope scope, string actorUserId, NewParcelSeason request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var season = request.Season?.Trim() ?? string.Empty;
        if (!SeasonShape().IsMatch(season)) errors.Add("invalid_season");
        var crop = Blank(request.DeclaredCrop);
        if (crop is not null && !CropShape().IsMatch(crop)) errors.Add("invalid_crop");
        if (request.DeclaredAreaFeddan is { } area && !(area > 0 && area <= MaxDeclaredFeddan)) errors.Add("invalid_declared_area");
        var sowing = Blank(request.DeclaredSowingDate);
        if (sowing is not null && !DateOnly.TryParseExact(sowing, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add("invalid_sowing_date");
        var externalId = ExternalId(request.ExternalId, errors);
        Throw(errors);

        var parcel = await store.Parcels.GetAsync(scope, request.ParcelId ?? string.Empty, ct) ?? throw new NotFoundException("parcel", request.ParcelId ?? string.Empty);
        var fields = await customFields.ValidateAsync(scope, CustomFieldEntities.ParcelSeason, request.CustomFields, ct);

        var existing = (await store.ParcelSeasons.FindAsync(scope, s => s.ParcelId == parcel.Id && s.Season == season, 1, ct)).FirstOrDefault();
        var row = existing ?? new ParcelSeason { ParcelId = parcel.Id, FarmId = parcel.FarmId, Season = season };
        row.DeclaredCrop = crop;
        row.DeclaredAreaFeddan = request.DeclaredAreaFeddan;
        row.DeclaredSowingDate = sowing;
        row.DeclaredSource = Blank(request.DeclaredSource);
        row.DeclaredAt = Now();
        row.ExternalId = externalId ?? existing?.ExternalId;
        if (request.CustomFields is not null || existing is null) row.CustomFields = fields;

        try
        {
            if (existing is null) await store.ParcelSeasons.InsertAsync(scope, row, ct);
            else await store.ParcelSeasons.ReplaceAsync(scope, row, ct);
        }
        catch (DuplicateKeyException)
        {
            throw new ValidationFailedException("duplicate_external_id");
        }

        await audit.RecordAsync(scope, actorUserId, "parcel_season.declared", "parcel_season", row.Id, ct: ct);
        return row;
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Required(string? value, int max, string code, List<string> errors)
    {
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0) errors.Add(code);
        else if (v.Length > max) errors.Add(code.Replace("required", "too_long", StringComparison.Ordinal));
        return v;
    }

    private static string? ExternalId(string? value, List<string> errors)
    {
        var v = Blank(value);
        if (v is { Length: > MaxExternalId }) errors.Add("invalid_external_id");
        return v;
    }

    private static void Throw(List<string> errors)
    {
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    private static async Task InsertAsync(Func<Task> insert)
    {
        try
        {
            await insert();
        }
        catch (DuplicateKeyException)
        {
            throw new ValidationFailedException("duplicate_external_id");
        }
    }
}
