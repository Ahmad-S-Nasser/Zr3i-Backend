using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;

namespace Zr3i.Geo;

/// <summary>
/// Limits applied to every boundary. Provisional: the Verification (R0) specification may tighten them.
/// The region is Egypt with a margin; it exists so a typo in a longitude cannot silently produce an area.
/// </summary>
public static class BoundaryRules
{
    public const int MaxVertices = 10_000;
    public const double MinAreaM2 = 1.0;
    public const double MaxAreaM2 = 20_000 * GeoConstants.SquareMetresPerFeddan;
    public const double MinLon = 24.0, MaxLon = 37.0, MinLat = 21.0, MaxLat = 32.0;

    /// <summary>Warn when the UTM 36N grid area differs from the true area by more than this fraction.</summary>
    public const double DistortionWarningFraction = 0.002;
}

public readonly record struct Bounds(double MinLon, double MinLat, double MaxLon, double MaxLat);

/// <summary>
/// The outcome of checking a polygon. Errors and warnings are stable codes, never prose: the screens, the import
/// error report and the API translate them. <see cref="Rings"/> holds the cleaned polygon (duplicate vertices dropped,
/// outer ring counter-clockwise, holes clockwise) and is what gets stored.
/// </summary>
public sealed record BoundaryCheck(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<IReadOnlyList<LonLat>> Rings,
    double AreaM2,
    double AreaFeddan,
    LonLat Centroid,
    Bounds Bounds)
{
    internal static BoundaryCheck Rejected(string code) =>
        new(false, [code], [], [], 0, 0, default, default);
}

public static class BoundaryValidator
{
    public static BoundaryCheck Check(PolygonInput input)
    {
        try
        {
            return CheckCore(input);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NetTopologySuite.Geometries.TopologyException or ArithmeticException)
        {
            return BoundaryCheck.Rejected("invalid_geometry");
        }
    }

    private static BoundaryCheck CheckCore(PolygonInput input)
    {
        if (input.Rings.Count == 0 || input.Rings[0].Count == 0) return BoundaryCheck.Rejected("too_few_vertices");
        if (input.Rings.Sum(r => r.Count) > BoundaryRules.MaxVertices) return BoundaryCheck.Rejected("too_many_vertices");
        if (input.Rings.SelectMany(r => r).Any(p => !InRange(p))) return BoundaryCheck.Rejected("coordinates_out_of_range");

        var rings = new List<List<LonLat>>();
        foreach (var raw in input.Rings)
        {
            var ring = DropConsecutiveDuplicates(raw);
            if (ring.Count < 2 || ring[0] != ring[^1]) return BoundaryCheck.Rejected(ring.Count >= 2 ? "ring_not_closed" : "too_few_vertices");
            if (ring.Count < 4) return BoundaryCheck.Rejected("too_few_vertices");
            rings.Add(ring);
        }

        if (rings.SelectMany(r => r).Any(p => p.Lon < BoundaryRules.MinLon || p.Lon > BoundaryRules.MaxLon || p.Lat < BoundaryRules.MinLat || p.Lat > BoundaryRules.MaxLat))
            return BoundaryCheck.Rejected("outside_supported_region");

        // Outer ring counter-clockwise, holes clockwise (RFC 7946).
        for (var i = 0; i < rings.Count; i++)
        {
            var ccw = SignedArea(rings[i]) > 0;
            if ((i == 0) != ccw) rings[i].Reverse();
        }

        var polygon = ToUtmPolygon([.. rings.Select(r => (IReadOnlyList<LonLat>)r)]);
        var validity = new IsValidOp(polygon).ValidationError;
        if (validity is not null)
        {
            return BoundaryCheck.Rejected(validity.ErrorType switch
            {
                TopologyValidationErrors.SelfIntersection or TopologyValidationErrors.RingSelfIntersection => "self_intersection",
                TopologyValidationErrors.HoleOutsideShell => "hole_outside_shell",
                _ => "invalid_geometry",
            });
        }

        var area = polygon.Area;
        if (area < BoundaryRules.MinAreaM2) return BoundaryCheck.Rejected("degenerate");
        if (area > BoundaryRules.MaxAreaM2) return BoundaryCheck.Rejected("implausible_size");

        var centre = polygon.Centroid;
        var centroid = Epsg32636.ToLonLat(centre.X, centre.Y);
        var outer = rings[0];
        var bounds = new Bounds(outer.Min(p => p.Lon), outer.Min(p => p.Lat), outer.Max(p => p.Lon), outer.Max(p => p.Lat));

        var warnings = new List<string>();
        if (Math.Abs(GridScaleFactor(centroid.Lon, centroid.Lat) - 1) > BoundaryRules.DistortionWarningFraction) warnings.Add("projection_distortion");

        return new BoundaryCheck(true, [], warnings, [.. rings.Select(r => (IReadOnlyList<LonLat>)r)], area,
            area / GeoConstants.SquareMetresPerFeddan, new LonLat(centroid.Lon, centroid.Lat), bounds);
    }

    /// <summary>Shoelace area in degrees: positive for counter-clockwise. Used only for orientation, never for measuring.</summary>
    public static double SignedArea(IReadOnlyList<LonLat> ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Count - 1; i++) sum += ring[i].Lon * ring[i + 1].Lat - ring[i + 1].Lon * ring[i].Lat;
        return sum / 2;
    }

    private static bool InRange(LonLat p) =>
        double.IsFinite(p.Lon) && double.IsFinite(p.Lat) && p.Lon is >= -180 and <= 180 && p.Lat is >= -90 and <= 90;

    private static List<LonLat> DropConsecutiveDuplicates(IReadOnlyList<LonLat> ring)
    {
        var result = new List<LonLat>(ring.Count);
        foreach (var p in ring)
            if (result.Count == 0 || result[^1] != p) result.Add(p);
        return result;
    }

    internal static Polygon ToUtmPolygon(IReadOnlyList<IReadOnlyList<LonLat>> rings)
    {
        var factory = new GeometryFactory();
        LinearRing ToRing(IReadOnlyList<LonLat> r) => factory.CreateLinearRing([.. r.Select(p => { var (e, n) = Epsg32636.FromLonLat(p.Lon, p.Lat); return new Coordinate(e, n); })]);
        return factory.CreatePolygon(ToRing(rings[0]), [.. rings.Skip(1).Select(ToRing)]);
    }

    /// <summary>Local linear scale of UTM zone 36N at a point; the grid area is the true area times its square.</summary>
    private static double GridScaleFactor(double lon, double lat)
    {
        var d = (lon - GeoConstants.CentralMeridian) * Math.PI / 180 * Math.Cos(lat * Math.PI / 180);
        var k = 0.9996 * (1 + d * d / 2);
        return k * k;
    }
}
