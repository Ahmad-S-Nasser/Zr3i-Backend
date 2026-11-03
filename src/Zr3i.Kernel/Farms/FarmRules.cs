using System.Globalization;
using System.Text.RegularExpressions;
using Zr3i.Geo;

namespace Zr3i.Kernel.Farms;

/// <summary>
/// The checks on a declared season, in one place so the services and the bulk import cannot disagree.
/// Pure functions: no database, no tenant.
/// </summary>
public static partial class FarmRules
{
    public const int MaxName = 200;
    public const int MaxExternalId = 100;
    public const double MaxDeclaredFeddan = BoundaryRules.MaxAreaM2 / GeoConstants.SquareMetresPerFeddan;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex SeasonShape();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial Regex CropShape();

    public static bool IsValidSeason(string? season) => season is not null && SeasonShape().IsMatch(season.Trim());

    public static bool IsValidDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Codes for a declared season: invalid_season, invalid_crop, invalid_declared_area, invalid_sowing_date. Blank optional values are fine.</summary>
    public static IReadOnlyList<string> SeasonErrors(string? season, string? crop, double? areaFeddan, string? sowingDate)
    {
        var errors = new List<string>();
        if (!IsValidSeason(season)) errors.Add("invalid_season");
        if (!string.IsNullOrWhiteSpace(crop) && !CropShape().IsMatch(crop.Trim())) errors.Add("invalid_crop");
        if (areaFeddan is { } area && !(area > 0 && area <= MaxDeclaredFeddan)) errors.Add("invalid_declared_area");
        if (!string.IsNullOrWhiteSpace(sowingDate) && !IsValidDate(sowingDate)) errors.Add("invalid_sowing_date");
        return errors;
    }
}
