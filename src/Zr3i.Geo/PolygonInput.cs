using System.Text.Json;

namespace Zr3i.Geo;

public readonly record struct LonLat(double Lon, double Lat);

/// <summary>A polygon as a customer supplies it: WGS84, first ring the outer boundary, any others holes.</summary>
public sealed record PolygonInput(IReadOnlyList<IReadOnlyList<LonLat>> Rings)
{
    public static PolygonInput FromExterior(IEnumerable<LonLat> exterior) => new([[.. exterior]]);
}

/// <summary>Reads a single polygon from GeoJSON: a Polygon geometry, a Feature, or a FeatureCollection holding one Feature.</summary>
public static class GeoJsonReader
{
    public const string Invalid = "invalid_geojson";
    public const string NotAPolygon = "not_a_polygon";

    /// <param name="errorCode">Null on success; otherwise a stable code from this class.</param>
    public static bool TryReadPolygon(string json, out PolygonInput? polygon, out string? errorCode)
    {
        polygon = null;
        errorCode = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var geometry = FindGeometry(doc.RootElement, out errorCode);
            if (geometry is null) return false;

            var g = geometry.Value;
            if (!g.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return Fail(Invalid, out errorCode);
            if (type.GetString() != "Polygon") return Fail(NotAPolygon, out errorCode);
            if (!g.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array) return Fail(Invalid, out errorCode);

            var rings = new List<IReadOnlyList<LonLat>>();
            foreach (var ring in coordinates.EnumerateArray())
            {
                if (ring.ValueKind != JsonValueKind.Array) return Fail(Invalid, out errorCode);
                var points = new List<LonLat>();
                foreach (var p in ring.EnumerateArray())
                {
                    // A position is [lon, lat] and may carry an altitude, which is ignored.
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) return Fail(Invalid, out errorCode);
                    if (p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number) return Fail(Invalid, out errorCode);
                    if (!p[0].TryGetDouble(out var lon) || !p[1].TryGetDouble(out var lat)) return Fail(Invalid, out errorCode);
                    points.Add(new LonLat(lon, lat));
                }

                rings.Add(points);
            }

            if (rings.Count == 0) return Fail(Invalid, out errorCode);
            polygon = new PolygonInput(rings);
            return true;
        }
        catch (JsonException)
        {
            return Fail(Invalid, out errorCode);
        }
    }

    private static JsonElement? FindGeometry(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            errorCode = Invalid;
            return null;
        }

        switch (type.GetString())
        {
            case "Feature":
                if (root.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object) return geometry;
                errorCode = Invalid;
                return null;
            case "FeatureCollection":
                if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array && features.GetArrayLength() == 1)
                    return FindGeometry(features[0], out errorCode);
                errorCode = Invalid;
                return null;
            default:
                return root;
        }
    }

    private static bool Fail(string code, out string? errorCode)
    {
        errorCode = code;
        return false;
    }
}
