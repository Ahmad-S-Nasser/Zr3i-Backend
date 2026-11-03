using MongoDB.Bson.Serialization.Attributes;

namespace Zr3i.Data;

/// <summary>
/// A polygon in the shape MongoDB's 2dsphere index expects ({ type, coordinates }, longitude first).
/// Plain data: the Kernel stores it on a boundary and never touches the driver.
/// </summary>
public sealed class GeoJsonPolygon
{
    [BsonElement("type")]
    public string Type { get; set; } = "Polygon";

    [BsonElement("coordinates")]
    public List<List<List<double>>> Coordinates { get; set; } = [];

    public static GeoJsonPolygon FromRings(IEnumerable<IEnumerable<(double Lon, double Lat)>> rings) =>
        new() { Coordinates = [.. rings.Select(r => r.Select(p => new List<double> { p.Lon, p.Lat }).ToList())] };
}
