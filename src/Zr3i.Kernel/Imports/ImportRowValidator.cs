using System.Globalization;
using Zr3i.Geo;
using Zr3i.Kernel.Farms;

namespace Zr3i.Kernel.Imports;

/// <summary>The columns an import file may have. Custom fields use a prefix: portfolio.key, farm.key, parcel.key, season.key.</summary>
public static class ImportColumns
{
    public const string ParcelExternalId = "parcel_external_id";
    public const string FarmExternalId = "farm_external_id";

    public static readonly IReadOnlyList<string> Required = [ParcelExternalId, FarmExternalId, "farm_name"];

    public static readonly IReadOnlyList<string> Known =
    [
        ParcelExternalId, FarmExternalId, "farm_name", "parcel_label", "portfolio_external_id", "portfolio_name",
        "governorate", "markaz", "village", "cadastral_ref",
        "season", "declared_crop", "declared_area_feddan", "declared_sowing_date", "declared_source",
        "boundary_geojson", "boundary_source", "captured_by", "captured_at", "accuracy_m",
    ];

    /// <summary>Prefix of a custom column mapped to the kind of record it belongs to.</summary>
    public static readonly IReadOnlyDictionary<string, string> CustomPrefixes = new Dictionary<string, string>
    {
        ["portfolio."] = CustomFieldEntities.Portfolio,
        ["farm."] = CustomFieldEntities.Farm,
        ["parcel."] = CustomFieldEntities.Parcel,
        ["season."] = CustomFieldEntities.ParcelSeason,
    };
}

/// <summary>One thing wrong with one cell. Codes are stable and machine-readable; the screens translate them.</summary>
public sealed class ImportRowError
{
    public string Field { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>A row that passed the checks, as typed values.</summary>
public sealed class ParsedRow
{
    public string ParcelExternalId { get; init; } = string.Empty;
    public string FarmExternalId { get; init; } = string.Empty;
    public string FarmName { get; init; } = string.Empty;
    public string ParcelLabel { get; init; } = string.Empty;
    public string? PortfolioExternalId { get; init; }
    public string? PortfolioName { get; init; }
    public string? Governorate { get; init; }
    public string? Markaz { get; init; }
    public string? Village { get; init; }
    public string? CadastralRef { get; init; }
    public string? Season { get; init; }
    public string? DeclaredCrop { get; init; }
    public double? DeclaredAreaFeddan { get; init; }
    public string? DeclaredSowingDate { get; init; }
    public string? DeclaredSource { get; init; }
    public PolygonInput? Boundary { get; init; }
    public BoundaryCheck? BoundaryCheck { get; init; }
    public string? BoundarySource { get; init; }
    public string? CapturedBy { get; init; }
    public DateTime? CapturedAt { get; init; }
    public double? AccuracyM { get; init; }
    public Dictionary<string, string> PortfolioFields { get; init; } = [];
    public Dictionary<string, string> FarmFields { get; init; } = [];
    public Dictionary<string, string> ParcelFields { get; init; } = [];
    public Dictionary<string, string> SeasonFields { get; init; } = [];
}

public sealed record RowValidation(ParsedRow? Row, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// Checks one row of an import file against the same rules the services use (<see cref="FarmRules"/>, <see cref="CustomFieldRules"/>,
/// <see cref="BoundaryValidator"/>). A pure function of the row and the tenant's custom field definitions: no database.
/// Column names are lower-case; the caller has already normalised them.
/// </summary>
public sealed class ImportRowValidator(IReadOnlyList<CustomFieldDefinition> definitions)
{
    public RowValidation Validate(IReadOnlyDictionary<string, string> raw)
    {
        var errors = new List<ImportRowError>();
        void Error(string field, string code) => errors.Add(new ImportRowError { Field = field, Code = code });
        string? Get(string name) => raw.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        // Identity
        string Required(string name, int max)
        {
            var v = Get(name);
            if (v is null) Error(name, "required");
            else if (v.Length > max) Error(name, "too_long");
            return v ?? string.Empty;
        }

        var parcelExternalId = Required(ImportColumns.ParcelExternalId, FarmRules.MaxExternalId);
        var farmExternalId = Required(ImportColumns.FarmExternalId, FarmRules.MaxExternalId);
        var farmName = Required("farm_name", FarmRules.MaxName);
        var parcelLabel = Get("parcel_label") ?? parcelExternalId;
        if (parcelLabel.Length > FarmRules.MaxName) Error("parcel_label", "too_long");
        var portfolioExternalId = Get("portfolio_external_id");
        if (portfolioExternalId is { Length: > FarmRules.MaxExternalId }) Error("portfolio_external_id", "too_long");

        // Season declaration
        var custom = CustomValues(raw);
        var season = Get("season");
        double? area = null;
        var declaredColumns = new[] { "declared_crop", "declared_area_feddan", "declared_sowing_date", "declared_source" };
        if (season is null && (declaredColumns.Any(c => Get(c) is not null) || custom["season."].Values.Any(v => !string.IsNullOrWhiteSpace(v))))
        {
            Error("season", "season_required");
        }
        else if (season is not null)
        {
            var areaText = Get("declared_area_feddan");
            var areaParsed = areaText is null || double.TryParse(areaText, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.IsFinite(a);
            if (!areaParsed) Error("declared_area_feddan", "invalid_number");
            else if (areaText is not null) area = double.Parse(areaText, NumberStyles.Float, CultureInfo.InvariantCulture);

            foreach (var code in FarmRules.SeasonErrors(season, Get("declared_crop"), area, Get("declared_sowing_date")))
                Error(code switch
                {
                    "invalid_season" => "season",
                    "invalid_crop" => "declared_crop",
                    "invalid_declared_area" => "declared_area_feddan",
                    _ => "declared_sowing_date",
                }, code);
        }

        // Boundary
        PolygonInput? boundary = null;
        BoundaryCheck? check = null;
        var geojson = Get("boundary_geojson");
        if (geojson is null)
        {
            if (new[] { "boundary_source", "captured_by", "captured_at", "accuracy_m" }.Any(c => Get(c) is not null)) Error("boundary_geojson", "required");
        }
        else if (!GeoJsonReader.TryReadPolygon(geojson, out boundary, out var geoError))
        {
            Error("boundary_geojson", geoError!);
        }
        else
        {
            check = BoundaryValidator.Check(boundary!);
            foreach (var code in check.Errors) Error("boundary_geojson", code);
        }

        var source = Get("boundary_source");
        if (source is not null && !BoundarySources.All.Contains(source)) Error("boundary_source", "invalid_source");

        DateTime? capturedAt = null;
        if (Get("captured_at") is { } at)
        {
            if (DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) capturedAt = parsed.UtcDateTime;
            else Error("captured_at", "invalid_date");
        }

        double? accuracy = null;
        if (Get("accuracy_m") is { } acc)
        {
            if (double.TryParse(acc, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && double.IsFinite(m) && m >= 0) accuracy = m;
            else Error("accuracy_m", "invalid_number");
        }

        // Custom fields: only for records the row actually touches.
        var fields = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (prefix, entity) in ImportColumns.CustomPrefixes)
        {
            var applies = prefix switch
            {
                "portfolio." => portfolioExternalId is not null,
                "season." => season is not null,
                _ => true,
            };
            var result = applies
                ? CustomFieldRules.Validate([.. definitions.Where(d => d.Entity == entity)], custom[prefix])
                : new CustomFieldResult(new Dictionary<string, string>(), []);
            foreach (var e in result.Errors) Error(prefix + e.Key, e.Code);
            fields[prefix] = new Dictionary<string, string>(result.Values);
        }

        if (errors.Count > 0) return new RowValidation(null, errors);

        return new RowValidation(new ParsedRow
        {
            ParcelExternalId = parcelExternalId,
            FarmExternalId = farmExternalId,
            FarmName = farmName,
            ParcelLabel = parcelLabel,
            PortfolioExternalId = portfolioExternalId,
            PortfolioName = Get("portfolio_name"),
            Governorate = Get("governorate"),
            Markaz = Get("markaz"),
            Village = Get("village"),
            CadastralRef = Get("cadastral_ref"),
            Season = season,
            DeclaredCrop = Get("declared_crop"),
            DeclaredAreaFeddan = area,
            DeclaredSowingDate = Get("declared_sowing_date"),
            DeclaredSource = Get("declared_source"),
            Boundary = boundary,
            BoundaryCheck = check,
            BoundarySource = source,
            CapturedBy = Get("captured_by"),
            CapturedAt = capturedAt,
            AccuracyM = accuracy,
            PortfolioFields = fields["portfolio."],
            FarmFields = fields["farm."],
            ParcelFields = fields["parcel."],
            SeasonFields = fields["season."],
        }, []);
    }

    /// <summary>Splits the "prefix.key" columns into one dictionary per kind of record, keeping every defined key (even if absent) for the required check.</summary>
    private Dictionary<string, Dictionary<string, string?>> CustomValues(IReadOnlyDictionary<string, string> raw)
    {
        var byPrefix = ImportColumns.CustomPrefixes.Keys.ToDictionary(p => p, _ => new Dictionary<string, string?>());
        foreach (var (column, value) in raw)
            foreach (var prefix in ImportColumns.CustomPrefixes.Keys)
                if (column.StartsWith(prefix, StringComparison.Ordinal)) byPrefix[prefix][column[prefix.Length..]] = value;
        return byPrefix;
    }
}

/// <summary>Checks that need the whole file: the same parcel and season twice, or rows that disagree about one farm, parcel or portfolio.</summary>
public sealed class CrossRowChecker
{
    private readonly HashSet<string> _parcelSeasons = [];
    private readonly Dictionary<string, string> _farms = [];
    private readonly Dictionary<string, string> _parcels = [];
    private readonly Dictionary<string, string> _portfolios = [];

    public IReadOnlyList<ImportRowError> Check(ParsedRow row)
    {
        var errors = new List<ImportRowError>();

        if (!_parcelSeasons.Add(row.ParcelExternalId + "\u001f" + (row.Season ?? string.Empty)))
            errors.Add(new ImportRowError { Field = ImportColumns.ParcelExternalId, Code = "duplicate_in_file" });

        Compare(_farms, row.FarmExternalId, Signature(row.FarmName, row.Governorate, row.Markaz, row.Village, row.CadastralRef, row.PortfolioExternalId, Flatten(row.FarmFields)),
            ImportColumns.FarmExternalId, "conflicting_farm_data", errors);
        Compare(_parcels, row.ParcelExternalId, Signature(row.FarmExternalId, row.ParcelLabel, Flatten(row.ParcelFields)),
            ImportColumns.ParcelExternalId, "conflicting_parcel_data", errors);
        if (row.PortfolioExternalId is not null)
            Compare(_portfolios, row.PortfolioExternalId, Signature(row.PortfolioName, Flatten(row.PortfolioFields)),
                "portfolio_external_id", "conflicting_portfolio_data", errors);

        return errors;
    }

    private static void Compare(Dictionary<string, string> seen, string key, string signature, string field, string code, List<ImportRowError> errors)
    {
        if (!seen.TryAdd(key, signature) && seen[key] != signature) errors.Add(new ImportRowError { Field = field, Code = code });
    }

    private static string Signature(params string?[] parts) => string.Join("\u001f", parts.Select(p => p ?? string.Empty));

    private static string Flatten(Dictionary<string, string> fields) => string.Join("\u001e", fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}
