using System.Globalization;
using System.Text.RegularExpressions;
using Zr3i.Data;

namespace Zr3i.Kernel.Farms;

public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Bool = "bool";
    public const string Choice = "choice";

    public static readonly IReadOnlyList<string> All = [Text, Number, Date, Bool, Choice];
}

/// <summary>The kinds of record a tenant can add its own fields to.</summary>
public static class CustomFieldEntities
{
    public const string Portfolio = "portfolio";
    public const string Farm = "farm";
    public const string Parcel = "parcel";
    public const string ParcelSeason = "parcel_season";

    public static readonly IReadOnlyList<string> All = [Portfolio, Farm, Parcel, ParcelSeason];
}

/// <summary>A field an organisation adds to its own farms, parcels and so on. Labels are required in both languages.</summary>
public sealed class CustomFieldDefinition : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;
    public string LabelAr { get; set; } = string.Empty;
    public string Type { get; set; } = CustomFieldTypes.Text;
    public List<string> Options { get; set; } = [];
    public bool Required { get; set; }
}

public sealed record CustomFieldError(string Key, string Code);

/// <summary>The checked values (as canonical strings) or the list of what is wrong.</summary>
public sealed record CustomFieldResult(IReadOnlyDictionary<string, string> Values, IReadOnlyList<CustomFieldError> Errors);

/// <summary>
/// Validates custom field values against the tenant's definitions. Values are stored as canonical strings
/// (numbers with the invariant culture, dates as yyyy-MM-dd, booleans as true/false) so they sort and compare the same everywhere.
/// </summary>
public static partial class CustomFieldRules
{
    public const int MaxTextLength = 500;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyShape();

    public static bool IsValidKey(string? key) => key is not null && KeyShape().IsMatch(key);

    public static CustomFieldResult Validate(IReadOnlyList<CustomFieldDefinition> definitions, IReadOnlyDictionary<string, string?>? values)
    {
        var errors = new List<CustomFieldError>();
        var clean = new Dictionary<string, string>();
        var byKey = definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

        foreach (var (key, raw) in values ?? new Dictionary<string, string?>())
        {
            if (!byKey.TryGetValue(key, out var definition)) { errors.Add(new CustomFieldError(key, "unknown_field")); continue; }

            var text = raw?.Trim();
            if (string.IsNullOrEmpty(text)) continue;   // blank means "not set"; Required is checked below

            var (ok, canonical, code) = Normalise(definition, text);
            if (ok) clean[key] = canonical!; else errors.Add(new CustomFieldError(key, code!));
        }

        foreach (var d in definitions.Where(d => d.Required && !clean.ContainsKey(d.Key) && !errors.Any(e => e.Key == d.Key)))
            errors.Add(new CustomFieldError(d.Key, "required"));

        return new CustomFieldResult(clean, errors);
    }

    private static (bool Ok, string? Value, string? Code) Normalise(CustomFieldDefinition d, string text)
    {
        switch (d.Type)
        {
            case CustomFieldTypes.Number:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                    ? (true, n.ToString("R", CultureInfo.InvariantCulture), null)
                    : (false, null, "invalid_number");
            case CustomFieldTypes.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (true, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (false, null, "invalid_date");
            case CustomFieldTypes.Bool:
                return text.Equals("true", StringComparison.OrdinalIgnoreCase) ? (true, "true", null)
                    : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? (true, "false", null)
                    : (false, null, "invalid_bool");
            case CustomFieldTypes.Choice:
                return d.Options.Contains(text, StringComparer.Ordinal) ? (true, text, null) : (false, null, "invalid_choice");
            default:
                return text.Length <= MaxTextLength ? (true, text, null) : (false, null, "too_long");
        }
    }
}
