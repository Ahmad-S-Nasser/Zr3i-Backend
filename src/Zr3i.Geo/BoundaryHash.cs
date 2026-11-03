using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zr3i.Geo;

/// <summary>
/// A fingerprint of a polygon's shape, so a re-import of the same boundary is recognised as unchanged. Coordinates are
/// rounded to 7 decimal places (about a centimetre, the precision they are stored at), each ring starts at its smallest
/// vertex, and holes are sorted, so the same shape written a different way gives the same hash.
/// </summary>
public static class BoundaryHash
{
    public static string Of(IReadOnlyList<IReadOnlyList<LonLat>> rings)
    {
        var parts = rings.Select(Canonical).ToList();
        if (parts.Count > 1) parts = [parts[0], .. parts.Skip(1).Order(StringComparer.Ordinal)];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));
    }

    private static string Canonical(IReadOnlyList<LonLat> ring)
    {
        var points = ring.Select(p => (Lon: Math.Round(p.Lon, 7), Lat: Math.Round(p.Lat, 7))).ToList();
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);   // closing vertex repeats the first

        var start = points.IndexOf(points.Min());
        var rotated = points.Skip(start).Concat(points.Take(start));
        return string.Join(';', rotated.Select(p => $"{p.Lon.ToString("F7", CultureInfo.InvariantCulture)},{p.Lat.ToString("F7", CultureInfo.InvariantCulture)}"));
    }
}
