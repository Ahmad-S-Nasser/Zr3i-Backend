using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;

namespace Zr3i.Modules.Verification;

/// <param name="ParcelsWithoutBoundary">Parcels with no boundary yet. Not readable and not unreadable: they are not in any class.</param>
public sealed record ReadabilityReport(
    string MethodId, double BufferMetres, int MinFullPixels, IReadOnlyList<ClassShare> Classes, int ParcelsWithoutBoundary);

/// <summary>
/// "Can this parcel be tested at 10 m at all?" for a whole book, by size class. Geometry only: it needs no imagery, so it is known
/// the moment a boundary is added. (Whether a given scene is clear enough is a separate, later question; see <see cref="SceneReadability"/>.)
/// </summary>
public sealed class ReadabilityService(KernelStore store)
{
    private const int Page = 500;

    public async Task<ReadabilityReport> ReportAsync(TenantScope scope, SizeClassScheme scheme, CancellationToken ct = default)
    {
        var parcels = new List<(double AreaFeddan, bool Readable)>();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(scope, p => p.CurrentBoundaryId != null, cursor, Page, ct);
            var ids = page.Items.Select(p => p.CurrentBoundaryId!).ToList();
            var boundaries = ids.Count == 0 ? [] : await store.Boundaries.FindAsync(scope, b => ids.Contains(b.Id), Page, ct);
            parcels.AddRange(boundaries.Select(b => (b.AreaFeddan, FullPixelsOf(b) >= PixelTestRules.MinFullPixels)));
            cursor = page.NextCursor;
        } while (cursor is not null);

        var without = (int)await store.Parcels.CountAsync(scope, p => p.CurrentBoundaryId == null, ct);
        return new ReadabilityReport(PixelTestRules.MethodId, PixelTestRules.BufferMetres, PixelTestRules.MinFullPixels, ReadableShare.ByClass(parcels, scheme), without);
    }

    /// <summary>The stored count, or one worked out from the shape for a boundary saved before the count was kept.</summary>
    internal static int FullPixelsOf(Zr3i.Kernel.Farms.BoundaryVersion b) =>
        b.PixelMethodId == PixelTestRules.MethodId
            ? b.FullPixels
            : PixelTest.Evaluate([.. b.Geometry.Coordinates.Select(r => (IReadOnlyList<LonLat>)[.. r.Select(p => new LonLat(p[0], p[1]))])]).FullPixels;
}
