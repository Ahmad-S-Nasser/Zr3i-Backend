using System.Security.Cryptography;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Methods;

namespace Zr3i.Imagery;

/// <summary>
/// Reads one scene for every parcel of the organisation whose current boundary lies under it, and records the readability of each
/// (<see cref="ParcelObservation"/>). A parcel too small to test is recorded as below resolution without reading any imagery. Parcels
/// are read in map order so neighbours reuse the blocks already fetched. Safe to run again: a parcel already recorded for this scene
/// and boundary version is left alone, so a job that was interrupted simply carries on.
/// </summary>
public sealed class IngestHandler(KernelStore store, IBandReader reader, MethodRegistry methods, TimeProvider clock) : IJobHandler
{
    private const int Page = 200;
    private static readonly string[] TenMetreBands = ["blue", "green", "red", "nir"];
    private static readonly string[] AllBands = [.. TenMetreBands, "scl"];

    public string Type => IngestPlanner.JobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var sceneId = context.Payload<IngestPayload>().SceneId;
        var scene = (await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SceneId == sceneId, 1, ct)).FirstOrDefault();
        if (scene is null) return JobOutcome.Failure("scene_not_found");
        if (scene.Epsg != IngestPlanner.SupportedEpsg) return JobOutcome.Success(JobJson.Serialize(new { unsupported_grid = true, scene.Epsg }));

        var method = await methods.GetAsync(R0Draft.MethodId, null, ct);
        var counts = new Counts();
        string? cursor = null;
        do
        {
            var page = await store.Parcels.FindPageAsync(scope, p => p.CurrentBoundaryId != null, cursor, Page, ct);
            await ProcessPageAsync(scope, scene, method, page.Items, counts, ct);
            await context.ReportProgressAsync(counts.Candidates, counts.Candidates, ct);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return JobOutcome.Success(JobJson.Serialize(new
        {
            scene = scene.SceneId, candidates = counts.Candidates, ingested = counts.Ingested, already = counts.Already,
            belowResolution = counts.BelowResolution, notReadable = counts.NotReadable,
        }));
    }

    private async Task ProcessPageAsync(TenantScope scope, Scene scene, MethodVersion? method, IReadOnlyList<Parcel> parcels, Counts counts, CancellationToken ct)
    {
        var boundaryIds = parcels.Select(p => p.CurrentBoundaryId!).ToList();
        var boundaries = (await store.Boundaries.FindAsync(scope, b => boundaryIds.Contains(b.Id), Page, ct)).ToDictionary(b => b.Id);
        var under = parcels.Where(p => boundaries.TryGetValue(p.CurrentBoundaryId!, out var b) && Overlaps(b, scene)).ToList();
        counts.Candidates += under.Count;
        if (under.Count == 0) return;

        var parcelIds = under.Select(p => p.Id).ToList();
        var done = (await store.ParcelObservations.FindAsync(scope, o => o.SceneId == scene.SceneId && parcelIds.Contains(o.ParcelId), Page, ct))
            .Select(o => (o.ParcelId, o.BoundaryId)).ToHashSet();

        var work = new List<(Parcel Parcel, BoundaryVersion Boundary, PixelTestResult Pixels)>();
        foreach (var parcel in under)
        {
            var boundary = boundaries[parcel.CurrentBoundaryId!];
            if (done.Contains((parcel.Id, boundary.Id))) { counts.Already++; continue; }
            work.Add((parcel, boundary, PixelTest.Evaluate(RingsOf(boundary), keepPixels: true)));
        }

        // Map order: parcels that sit next to each other are read one after the other, so the blocks fetched for one serve the next.
        work.Sort((a, b) => a.Pixels.Pixels!.Count == 0 || b.Pixels.Pixels!.Count == 0 ? 0
            : Compare(a.Pixels.Pixels.Min(p => (p.Row, p.Col)), b.Pixels.Pixels.Min(p => (p.Row, p.Col))));

        var results = new List<ParcelObservation>(work.Count);
        foreach (var (parcel, boundary, pixels) in work)
            results.Add(await ObserveAsync(scope, scene, method, parcel, boundary, pixels, counts, ct));

        try
        {
            await store.ParcelObservations.InsertManyAsync(scope, results, ct);
        }
        catch (DuplicateKeyException)
        {
            // Another worker recorded some of these at the same moment; they are the same facts, so there is nothing to repair.
        }

        counts.Ingested += results.Count;
    }

    private async Task<ParcelObservation> ObserveAsync(
        TenantScope scope, Scene scene, MethodVersion? method, Parcel parcel, BoundaryVersion boundary, PixelTestResult pixels, Counts counts, CancellationToken ct)
    {
        var obs = new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = boundary.Id, SceneId = scene.SceneId, SensingTime = scene.SensingTime, FullPixels = pixels.FullPixels,
            SceneItemSha256 = scene.ItemSha256, PixelMethodId = pixels.MethodId, Reader = reader.Name, Attribution = scene.Attribution,
            MethodId = method?.MethodId ?? string.Empty, MethodVersion = method?.Version ?? 0, MethodState = method?.State ?? string.Empty,
            IngestedAt = clock.GetUtcNow().UtcDateTime,
        };

        if (!pixels.Passes)
        {
            counts.BelowResolution++;
            obs.Readable = false;
            obs.Reason = ObservationReasons.BelowResolution;
            return obs;
        }

        var lattice = pixels.Pixels!.OrderBy(p => p.Row).ThenBy(p => p.Col).ToList();
        var windows = await Task.WhenAll(AllBands.Select(b => reader.ReadAsync(scene.Assets[b], lattice, ct)));
        var byBand = AllBands.Zip(windows).ToDictionary(x => x.First, x => x.Second);

        var scl = byBand["scl"];
        var scene2 = SceneReadability.Evaluate(lattice, (c, r) => scl.ValueAt(c, r));
        var histogram = new Dictionary<string, int>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var (c, r) in lattice)
        {
            foreach (var band in AllBands)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, byBand[band].ValueAt(c, r));
                hash.AppendData(buffer);
            }

            var cls = scl.ValueAt(c, r).ToString(System.Globalization.CultureInfo.InvariantCulture);
            histogram[cls] = histogram.GetValueOrDefault(cls) + 1;
        }

        obs.UsablePixels = scene2.Usable;
        obs.LostPixels = scene2.Lost;
        obs.LostShare = scene2.LostShare;
        obs.Readable = scene2.Readable;
        obs.Reason = scene2.Readable ? null : ObservationReasons.SceneLost;
        obs.SclHistogram = histogram;
        obs.Bands = [.. AllBands];
        obs.PixelStackSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!obs.Readable) counts.NotReadable++;
        else
        {
            var usable = lattice.Where(p => Zr3i.Geo.PixelTestRules.UsableClasses.Contains(scl.ValueAt(p.Col, p.Row)))
                .Select(p => (byBand["nir"].ValueAt(p.Col, p.Row), byBand["red"].ValueAt(p.Col, p.Row)));
            if (NdviSummary.Of(usable, new SceneRadiometry(scene.BoaOffsetApplied, scene.ProcessingBaseline)) is { } ndvi)
            {
                obs.NdviPixels = ndvi.Count;
                obs.NdviExcluded = ndvi.NoData + ndvi.Implausible;
                obs.NdviMedian = ndvi.Median;
                obs.NdviMean = ndvi.Mean;
                obs.NdviMin = ndvi.Min;
                obs.NdviMax = ndvi.Max;
            }
        }
        return obs;
    }

    private static bool Overlaps(BoundaryVersion b, Scene s) =>
        b.MinLon <= s.MaxLon && b.MaxLon >= s.MinLon && b.MinLat <= s.MaxLat && b.MaxLat >= s.MinLat;

    private static IReadOnlyList<IReadOnlyList<LonLat>> RingsOf(BoundaryVersion b) =>
        [.. b.Geometry.Coordinates.Select(r => (IReadOnlyList<LonLat>)[.. r.Select(p => new LonLat(p[0], p[1]))])];

    private static int Compare((int Row, int Col) a, (int Row, int Col) b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col);

    private sealed class Counts
    {
        public int Candidates, Ingested, Already, BelowResolution, NotReadable;
    }
}
