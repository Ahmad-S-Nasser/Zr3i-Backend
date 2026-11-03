using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace Zr3i.Geo;

/// <summary>
/// The parameters of the Sentinel-2 pixel test (confirmed defaults, awaiting the R0 specification; changing any is a new method id).
/// A "full pixel" is a whole 10 m pixel inside the parcel shrunk by a fixed 5 m with round corners.
/// </summary>
public static class PixelTestRules
{
    public const string MethodId = "GEO-PIXEL-2026-01";
    public const double BufferMetres = 5.0;
    public const double PixelMetres = 10.0;
    public const int MinFullPixels = 8;

    /// <summary>A scene is unreadable for a parcel when this share of its full pixels (or more) is lost.</summary>
    public const double MaxLostShare = 0.6;

    /// <summary>
    /// Numerical tolerance, one micrometre: a pixel counts if it lies inside the shrunk shape give or take this much. Coordinates are
    /// stored to about a centimetre, so this changes no real result; it only stops floating-point noise deciding a pixel whose edge
    /// sits exactly on the shape's edge.
    /// </summary>
    public const double ToleranceMetres = 1e-6;

    /// <summary>Scene classification classes that count as usable: vegetation, not vegetated, water.</summary>
    public static readonly IReadOnlyList<int> UsableClasses = [4, 5, 6];
}

/// <param name="Pixels">The grid positions (column, row) of the full pixels, only when asked for. Column = floor(easting/10), row = floor(northing/10).</param>
public sealed record PixelTestResult(int FullPixels, bool Passes, string MethodId, IReadOnlyList<(int Col, int Row)>? Pixels);

/// <summary>
/// Can a parcel be tested at 10 m at all? Counts the whole pixels of the Sentinel-2 lattice that lie inside the parcel after a 5 m inward
/// buffer. Fewer than eight means "below resolution": the parcel is too small to test, which is never the same as "no crop".
/// Pixel corners sit on multiples of 10 m in the UTM grid (checked against a real tile in the W1-5 spike).
/// </summary>
public static class PixelTest
{
    public static PixelTestResult Evaluate(IReadOnlyList<IReadOnlyList<LonLat>> rings, bool keepPixels = false)
    {
        var inner = BoundaryValidator.ToUtmPolygon(rings).Buffer(-PixelTestRules.BufferMetres);
        var pixels = keepPixels ? new List<(int, int)>() : null;
        var count = 0;

        if (!inner.IsEmpty)
        {
            var prepared = PreparedGeometryFactory.Prepare(inner);
            var factory = inner.Factory;
            var env = inner.EnvelopeInternal;
            var size = PixelTestRules.PixelMetres;
            int c0 = (int)Math.Floor(env.MinX / size), c1 = (int)Math.Ceiling(env.MaxX / size);
            int r0 = (int)Math.Floor(env.MinY / size), r1 = (int)Math.Ceiling(env.MaxY / size);

            for (var r = r0; r < r1; r++)
            {
                for (var c = c0; c < c1; c++)
                {
                    double x = c * size, y = r * size;
                    // A whole pixel inside the shape has its centre inside too, so the cheap point test goes first.
                    if (!prepared.Contains(factory.CreatePoint(new Coordinate(x + size / 2, y + size / 2)))) continue;
                    const double e = PixelTestRules.ToleranceMetres;
                    var square = factory.CreatePolygon([new(x + e, y + e), new(x + size - e, y + e), new(x + size - e, y + size - e), new(x + e, y + size - e), new(x + e, y + e)]);
                    if (!prepared.Contains(square)) continue;
                    count++;
                    pixels?.Add((c, r));
                }
            }
        }

        return new PixelTestResult(count, count >= PixelTestRules.MinFullPixels, PixelTestRules.MethodId, pixels);
    }
}

/// <param name="Lost">Full pixels that are not usable: cloud, shadow, cirrus, saturated, no data.</param>
public sealed record SceneReadabilityResult(int Total, int Usable, int Lost, double LostShare, bool Readable);

/// <summary>
/// Is one scene usable for one parcel? Counts the parcel's full pixels whose scene classification is usable. The scene is readable
/// only if it has at least one full pixel and fewer than 60 percent of them are lost: the limit itself fails.
/// </summary>
public static class SceneReadability
{
    /// <param name="classAt">The scene classification at a pixel of the 10 m lattice, or null where the scene has no value.</param>
    public static SceneReadabilityResult Evaluate(IReadOnlyList<(int Col, int Row)> fullPixels, Func<int, int, int?> classAt)
    {
        var usable = fullPixels.Count(p => classAt(p.Col, p.Row) is { } c && PixelTestRules.UsableClasses.Contains(c));
        var lost = fullPixels.Count - usable;
        var share = fullPixels.Count == 0 ? 1.0 : (double)lost / fullPixels.Count;

        // Integer form of "lost < 60 percent" (3/5), so the limit is decided exactly.
        var readable = fullPixels.Count > 0 && lost * 5 < fullPixels.Count * 3;
        return new SceneReadabilityResult(fullPixels.Count, usable, lost, share, readable);
    }
}
