using System.Security.Cryptography;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Chips;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Imagery;

/// <summary>
/// Makes the true-colour and NDVI pictures for one parcel and one scene. It reads the bands for a small window around the parcel (the same
/// <see cref="IBandReader"/> the ingest job uses), draws both pictures, writes the files and records one row for each. Safe to run twice: a picture
/// already recorded for this parcel, boundary, scene and kind is left alone, and the files hold the same bytes anyway.
/// </summary>
public sealed class ChipHandler(KernelStore store, IBandReader reader, IChipStore files, TimeProvider clock) : IJobHandler
{
    private const int Padding = 8;
    private const int TargetPixels = 320;
    private static readonly string[] Bands = ["blue", "green", "red", "nir", "scl"];

    public string Type => ChipPlanner.JobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var payload = context.Payload<ChipPayload>();
        var parcel = await store.Parcels.GetAsync(scope, payload.ParcelId, ct);
        if (parcel?.CurrentBoundaryId is null) return JobOutcome.Failure("parcel_not_found");
        var scene = (await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SceneId == payload.SceneId, 1, ct)).FirstOrDefault();
        if (scene is null) return JobOutcome.Failure("scene_not_found");
        var boundary = await store.Boundaries.GetAsync(scope, parcel.CurrentBoundaryId, ct);
        if (boundary is null) return JobOutcome.Failure("boundary_not_found");
        var observation = (await store.ParcelObservations.FindAsync(scope, o => o.ParcelId == parcel.Id && o.SceneId == scene.SceneId && o.BoundaryId == boundary.Id, 1, ct)).FirstOrDefault();
        if (observation is null) return JobOutcome.Failure("not_observed");
        if (observation.Reason == ObservationReasons.BelowResolution) return JobOutcome.Failure("below_resolution");
        if (!observation.Readable) return JobOutcome.Failure("not_readable");

        var existing = await store.ParcelChips.FindAsync(scope, c => c.ParcelId == parcel.Id && c.SceneId == scene.SceneId && c.BoundaryId == boundary.Id, 10, ct);
        if (ChipKinds.All.All(k => existing.Any(c => c.Kind == k))) return JobOutcome.Success(JobJson.Serialize(new { already = true }));

        var rings = (IReadOnlyList<IReadOnlyList<LonLat>>)[.. boundary.Geometry.Coordinates.Select(r => (IReadOnlyList<LonLat>)[.. r.Select(p => new LonLat(p[0], p[1]))])];
        var measured = PixelTest.Evaluate(rings, keepPixels: true).Pixels ?? [];
        var declared = rings[0].Select(p => Epsg32636.FromLonLat(p.Lon, p.Lat)).Select(p => (p.Easting, p.Northing)).ToList();

        // The window: the declared boundary's box in 10 m lattice pixels, padded, and never larger than the cap.
        int c0 = (int)Math.Floor(declared.Min(p => p.Easting) / 10.0) - Padding, c1 = (int)Math.Ceiling(declared.Max(p => p.Easting) / 10.0) + Padding;
        int r0 = (int)Math.Floor(declared.Min(p => p.Northing) / 10.0) - Padding, r1 = (int)Math.Ceiling(declared.Max(p => p.Northing) / 10.0) + Padding;
        int width = Math.Min(c1 - c0, ChipRenderer.MaxWindow), height = Math.Min(r1 - r0, ChipRenderer.MaxWindow);
        var scale = Math.Clamp(TargetPixels / Math.Max(width, height), 2, 12);

        var lattice = new List<(int Col, int Row)>(width * height);
        for (var r = r0; r < r0 + height; r++)
            for (var c = c0; c < c0 + width; c++) lattice.Add((c, r));

        var windows = await Task.WhenAll(Bands.Select(b => reader.ReadAsync(scene.Assets[b], lattice, ct)));
        var byBand = Bands.Zip(windows).ToDictionary(x => x.First, x => x.Second);
        ChipPixel At(int c, int r) => new(byBand["blue"].ValueAt(c, r), byBand["green"].ValueAt(c, r), byBand["red"].ValueAt(c, r), byBand["nir"].ValueAt(c, r), byBand["scl"].ValueAt(c, r));

        var images = ChipRenderer.Render(new ChipInput(c0, r0, width, height, scale, At, new SceneRadiometry(scene.BoaOffsetApplied, scene.ProcessingBaseline), declared, measured.ToHashSet()));

        foreach (var (kind, bytes) in new[] { (ChipKinds.TrueColour, images.TrueColour), (ChipKinds.Ndvi, images.Ndvi) })
        {
            if (existing.Any(c => c.Kind == kind)) continue;
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var path = await files.WriteAsync(scope.TenantId, parcel.Id, scene.SceneId, kind, bytes, sha, ct);
            var chip = new ParcelChip
            {
                ParcelId = parcel.Id, BoundaryId = boundary.Id, SceneId = scene.SceneId, Kind = kind, Path = path, Sha256 = sha, Bytes = bytes.Length,
                WidthPx = images.WidthPx, HeightPx = images.HeightPx, Scale = scale, OriginCol = c0, OriginRow = r0, SensingTime = scene.SensingTime,
                SceneItemSha256 = scene.ItemSha256, PixelStackSha256 = observation.PixelStackSha256 ?? string.Empty, PixelMethodId = observation.PixelMethodId, Reader = reader.Name,
                Attribution = scene.Attribution, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            try
            {
                await store.ParcelChips.InsertAsync(scope, chip, ct);
            }
            catch (DuplicateKeyException)
            {
                // Another worker made the same picture at the same moment; they are the same bytes.
            }
        }

        return JobOutcome.Success(JobJson.Serialize(new { scene = scene.SceneId, parcel = parcel.Id, width = images.WidthPx, height = images.HeightPx }));
    }
}
