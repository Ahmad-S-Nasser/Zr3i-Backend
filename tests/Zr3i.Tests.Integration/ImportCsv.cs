using System.Globalization;
using System.Text;
using Zr3i.Geo;

namespace Zr3i.Tests.Integration;

/// <summary>Builds import files for tests: one dictionary per row, written with every field quoted.</summary>
public static class ImportCsv
{
    public static readonly string[] Columns =
    [
        "parcel_external_id", "farm_external_id", "farm_name", "parcel_label", "portfolio_external_id", "portfolio_name", "governorate",
        "season", "declared_crop", "declared_area_feddan", "boundary_geojson", "captured_at",
    ];

    /// <summary>A valid row for parcel number i. Parcels share a farm in groups of five; each has a distinct square near the Nile Delta.</summary>
    public static Dictionary<string, string> Row(int i, double side = 150)
    {
        var farm = i / 5;
        return new Dictionary<string, string>
        {
            ["parcel_external_id"] = $"P-{i:D5}",
            ["farm_external_id"] = $"F-{farm:D4}",
            ["farm_name"] = $"Farm {farm}",
            ["parcel_label"] = $"Parcel {i}",
            ["portfolio_external_id"] = "PF-1",
            ["portfolio_name"] = "Delta book",
            ["governorate"] = "Kafr El Sheikh",
            ["season"] = "2026-27-winter",
            ["declared_crop"] = "beet",
            ["declared_area_feddan"] = "5",
            ["boundary_geojson"] = Square(i, side),
        };
    }

    /// <summary>A square on a grid of 300 m cells, 300 to a row (so no two overlap, and 100,000 fit inside Egypt), as GeoJSON in lon/lat.</summary>
    public static string Square(int i, double side)
    {
        var e = 300000 + (i % 300) * 300;
        var n = 3400000 + (i / 300) * 300;
        var coords = new (double, double)[] { (e, n), (e + side, n), (e + side, n + side), (e, n + side), (e, n) }
            .Select(p => { var (lon, lat) = Epsg32636.ToLonLat(p.Item1, p.Item2); return $"[{lon.ToString("F7", CultureInfo.InvariantCulture)},{lat.ToString("F7", CultureInfo.InvariantCulture)}]"; });
        return "{\"type\":\"Polygon\",\"coordinates\":[[" + string.Join(",", coords) + "]]}";
    }

    public static string BowTie() =>
        "{\"type\":\"Polygon\",\"coordinates\":[[[31.0,30.8],[31.001,30.801],[31.001,30.8],[31.0,30.801],[31.0,30.8]]]}";

    public static string Build(IEnumerable<Dictionary<string, string>> rows, IEnumerable<string>? columns = null, char delimiter = ',')
    {
        var cols = (columns ?? Columns).ToList();
        var sb = new StringBuilder();
        sb.Append(string.Join(delimiter, cols)).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(delimiter, cols.Select(c => Quote(row.GetValueOrDefault(c, string.Empty))))).Append("\r\n");
        return sb.ToString();
    }

    public static MemoryStream Stream(string csv) => new(new UTF8Encoding(false).GetBytes(csv));

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
