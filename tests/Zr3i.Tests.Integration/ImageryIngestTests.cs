using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Geo;
using Zr3i.Imagery;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ImageryIngestTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("ingest-a");
    private static readonly TenantScope B = TenantScope.ForTenant("ingest-b");
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly RasterGrid TenGrid = new(199980, 3500040, 10);

    /// <summary>A synthetic raster on tile 36RTV's real geometry. Class and digital number come from functions of the cell, so expected values can be worked out independently.</summary>
    private sealed class FakeBandReader(Func<int, int, int> sclAt, Func<string, int, int, int>? dnAt = null) : IBandReader
    {
        public List<(string Band, PixelWindow Window)> Reads { get; } = [];
        public string Name => "fake 1.0";

        public Task<BandWindow> ReadAsync(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct = default)
        {
            var grid = href == "scl" ? new RasterGrid(199980, 3500040, 20) : TenGrid;
            var window = grid.WindowOf(latticePixels);
            lock (Reads) Reads.Add((href, window));
            var values = new int[window.Cells];
            for (var r = 0; r < window.Height; r++)
                for (var c = 0; c < window.Width; c++)
                {
                    int col = window.XOff + c, row = window.YOff + r;
                    values[r * window.Width + c] = href == "scl" ? sclAt(col, row) : (dnAt ?? ((_, x, y) => 400 + (x * 7 + y * 3) % 200))(href, col, row);
                }

            return Task.FromResult(new BandWindow(values, window, grid));
        }
    }

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        await k.Methods.EnsureBuiltInsAsync();
        return k;
    }

    private static Scene Tile36RTV(string id = "S2C_36RTV_20261027_0_L2A", int epsg = 32636) => new()
    {
        SceneId = id, Source = "fake", Collection = "sentinel-2-l2a", Platform = "sentinel-2c", SensingTime = new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc),
        Tile = "36RTV", Epsg = epsg, CloudCover = 1, ProcessingBaseline = "05.13", BoaOffsetApplied = true, ItemSha256 = new string('C', 64),
        Attribution = "Contains modified Copernicus Sentinel data 2026", MinLon = 30.0, MinLat = 30.5, MaxLon = 31.2, MaxLat = 31.5,
        Assets = new() { ["blue"] = "blue", ["green"] = "green", ["red"] = "red", ["nir"] = "nir", ["scl"] = "scl" },
    };

    private static async Task<Parcel> ParcelAsync(KernelHarness k, TenantScope scope, double side, double eastOffset, double northOffset, string label)
    {
        var farm = await k.Farms.CreateFarmAsync(scope, "u", new NewFarm("F " + label));
        var parcel = await k.Farms.CreateParcelAsync(scope, "u", new NewParcel(farm.Id, label));
        await k.Boundaries.AddAsync(scope, "u", parcel.Id, KernelHarness.SquareMetres(side, 303800 + eastOffset, 3409200 + northOffset), new BoundaryProvenance("survey", "s", Captured, 1));
        return parcel;
    }

    private static JobRunner Runner(KernelHarness k, IBandReader reader) =>
        new(k.Data.CreateJobClaimer(new JobQueueOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.Zero)), [new IngestHandler(k.Store, reader, k.Methods, k.Clock)], k.Clock, "ingest-worker");

    private static async Task<string> RunAsync(KernelHarness k, IBandReader reader, TenantScope scope, string sceneId)
    {
        var job = await k.Data.Jobs.EnqueueAsync(scope, IngestPlanner.JobType, JobJson.Serialize(new IngestPayload(sceneId)), "u", k.Clock.GetUtcNow().UtcDateTime);
        await Runner(k, reader).RunUntilIdleAsync();
        var done = (await k.Data.Jobs.GetAsync(scope, job.Id))!;
        return done.Status == "succeeded" ? done.ResultJson! : done.Status + ":" + done.ErrorCode;
    }

    private static bool Usable(int cls) => cls is 4 or 5 or 6;

    [Fact]
    public async Task EachParcelGetsAnObservationWhoseCountsMatchAnIndependentCalculationAndWhoseProvenanceIsComplete()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        var parcel = await ParcelAsync(k, A, 150, 0, 0, "p1");
        // Every fifth 20 m classification cell is cloud, so some of the parcel's pixels are lost.
        var reader = new FakeBandReader((c, r) => c % 5 == 0 ? 9 : 4);
        var boundary = (await k.Boundaries.GetCurrentAsync(A, parcel.Id))!;
        var pixels = PixelTest.Evaluate([.. boundary.Geometry.Coordinates.Select(r => (IReadOnlyList<LonLat>)[.. r.Select(p => new LonLat(p[0], p[1]))])], keepPixels: true).Pixels!;
        var scl20 = new RasterGrid(199980, 3500040, 20);
        var expectedLost = pixels.Count(p => !Usable(scl20.CellOf(p.Col, p.Row).Col % 5 == 0 ? 9 : 4));

        var result = await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");

        var o = (await k.Store.ParcelObservations.FindAsync(A, _ => true)).Single();
        Assert.Contains("\"ingested\":1", result, StringComparison.Ordinal);
        Assert.Equal((pixels.Count, expectedLost, pixels.Count - expectedLost), (o.FullPixels, o.LostPixels, o.UsablePixels));
        Assert.Equal(o.LostPixels / (double)o.FullPixels, o.LostShare, precision: 9);
        Assert.Equal(o.LostPixels * 5 < o.FullPixels * 3, o.Readable);
        Assert.True(o.LostPixels > 0);
        Assert.Equal(o.FullPixels, o.SclHistogram.Values.Sum());
        Assert.Equal((parcel.Id, boundary.Id), (o.ParcelId, o.BoundaryId));

        Assert.Equal(("S2C_36RTV_20261027_0_L2A", new string('C', 64), "GEO-PIXEL-2026-01"), (o.SceneId, o.SceneItemSha256, o.PixelMethodId));
        Assert.Equal(("ZR3I-R0-2026-01", 1, "draft", "fake 1.0"), (o.MethodId, o.MethodVersion, o.MethodState, o.Reader));
        Assert.Equal(["blue", "green", "red", "nir", "scl"], o.Bands);
        Assert.Equal("Contains modified Copernicus Sentinel data 2026", o.Attribution);
        Assert.Matches("^[0-9A-F]{64}$", o.PixelStackSha256!);
        Assert.Equal(new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc), o.SensingTime);
    }

    [Fact]
    public async Task ATinyParcelIsRecordedAsBelowResolutionWithoutReadingAnyImagery()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        var tiny = await ParcelAsync(k, A, 30, 0, 0, "tiny");
        var reader = new FakeBandReader((_, _) => 4);

        await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");

        var o = (await k.Store.ParcelObservations.FindAsync(A, _ => true)).Single();
        Assert.Equal(tiny.Id, o.ParcelId);
        Assert.Equal((false, ObservationReasons.BelowResolution, null), (o.Readable, o.Reason, o.PixelStackSha256));
        Assert.True(o.FullPixels < 8);
        Assert.Empty(reader.Reads);                                         // nothing was fetched for it
    }

    [Fact]
    public async Task AParcelUnderCloudIsStillRecordedAsNotReadableNeverDropped()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        await ParcelAsync(k, A, 150, 0, 0, "cloudy");

        await RunAsync(k, new FakeBandReader((_, _) => 9), A, "S2C_36RTV_20261027_0_L2A");

        var o = (await k.Store.ParcelObservations.FindAsync(A, _ => true)).Single();
        Assert.Equal((false, ObservationReasons.SceneLost, 0, 1.0), (o.Readable, o.Reason, o.UsablePixels, o.LostShare));
        Assert.Equal(new[] { "9" }, o.SclHistogram.Keys);
    }

    [Fact]
    public async Task RunningTheSameSceneAgainAddsNothingAndAReCaptureAddsOnlyTheNewVersion()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        var parcel = await ParcelAsync(k, A, 150, 0, 0, "p");
        var reader = new FakeBandReader((_, _) => 4);

        await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");
        var again = await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");
        Assert.Contains("\"already\":1", again, StringComparison.Ordinal);
        Assert.Equal(1, await k.Store.ParcelObservations.FindAsync(A, _ => true).ContinueWith(t => t.Result.Count));

        await k.Boundaries.AddAsync(A, "u", parcel.Id, KernelHarness.SquareMetres(200), new BoundaryProvenance("survey", "s2", Captured.AddDays(1), 0.5));
        await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");

        var all = await k.Store.ParcelObservations.FindAsync(A, _ => true);
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Select(o => o.BoundaryId).Distinct().Count());
        Assert.True(all.Single(o => o.BoundaryId == (k.Boundaries.GetCurrentAsync(A, parcel.Id).Result)!.Id).FullPixels > all.Min(o => o.FullPixels));
    }

    [Fact]
    public async Task OnlyParcelsUnderTheSceneAndOnlyTheRequestingOrganisationAreRead()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        await ParcelAsync(k, A, 150, 0, 0, "under-the-scene");
        await ParcelAsync(k, B, 150, 300, 0, "other-tenant");
        var farm = await k.Farms.CreateFarmAsync(A, "u", new NewFarm("Far away"));
        var far = await k.Farms.CreateParcelAsync(A, "u", new NewParcel(farm.Id, "far"));
        await k.Boundaries.AddAsync(A, "u", far.Id, PolygonInput.FromExterior([new(26.0, 29.0), new(26.003, 29.0), new(26.003, 29.003), new(26.0, 29.003), new(26.0, 29.0)]),
            new BoundaryProvenance("survey", "s", Captured, 1));                                    // outside the scene's footprint

        var result = await RunAsync(k, new FakeBandReader((_, _) => 4), A, "S2C_36RTV_20261027_0_L2A");

        Assert.Contains("\"candidates\":1", result, StringComparison.Ordinal);
        Assert.Single(await k.Store.ParcelObservations.FindAsync(A, _ => true));
        Assert.Empty(await k.Store.ParcelObservations.FindAsync(B, _ => true));
    }

    [Fact]
    public async Task OnlyTheWindowAParcelNeedsIsReadAndNeighboursAreReadInMapOrder()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        foreach (var (e, n, label) in new[] { (900.0, 600.0, "d"), (0.0, 0.0, "a"), (600.0, 0.0, "c"), (300.0, 0.0, "b"), (0.0, 600.0, "e") })
            await ParcelAsync(k, A, 150, e, n, label);
        var reader = new FakeBandReader((_, _) => 4);

        await RunAsync(k, reader, A, "S2C_36RTV_20261027_0_L2A");

        var red = reader.Reads.Where(r => r.Band == "red").Select(r => r.Window).ToList();
        Assert.Equal(5, red.Count);
        Assert.All(red, w => Assert.InRange(w.Cells, 1, 20 * 20));          // a 150 m parcel is about 15 x 15 cells, never a tile
        Assert.Equal(25, reader.Reads.Count);                                // five bands per parcel
        // The order follows the map: southern rows (larger raster row numbers) have smaller northings, so ascending northing = descending raster row.
        var keys = red.Select(w => (-w.YOff, w.XOff)).ToList();
        Assert.Equal(keys.Order(), keys);
    }

    [Fact]
    public async Task ThePixelHashIsStableAndChangesWithAnyValueRead()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV()]);
        await ParcelAsync(k, A, 150, 0, 0, "p1");
        await ParcelAsync(k, B, 150, 0, 0, "p1-same-place");           // the same shape, another organisation

        await RunAsync(k, new FakeBandReader((_, _) => 4), A, "S2C_36RTV_20261027_0_L2A");
        await RunAsync(k, new FakeBandReader((_, _) => 4), B, "S2C_36RTV_20261027_0_L2A");
        var same = (await k.Store.ParcelObservations.FindAsync(B, _ => true)).Single().PixelStackSha256;
        Assert.Equal((await k.Store.ParcelObservations.FindAsync(A, _ => true)).Single().PixelStackSha256, same);

        var k2 = await NewAsync();
        await k2.Scenes.UpsertAsync([Tile36RTV()]);
        await ParcelAsync(k2, A, 150, 0, 0, "p1");
        await RunAsync(k2, new FakeBandReader((_, _) => 4, (band, c, r) => band == "nir" && c == 10390 && r == 9078 ? 9999 : 400 + (c * 7 + r * 3) % 200), A, "S2C_36RTV_20261027_0_L2A");
        Assert.NotEqual(same, (await k2.Store.ParcelObservations.FindAsync(A, _ => true)).Single().PixelStackSha256);
    }

    [Fact]
    public async Task ASceneOnAnotherGridOrMissingFromTheCatalogueIsHandledWithoutGuessing()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([Tile36RTV("S2C_35RQQ_X", epsg: 32635)]);
        await ParcelAsync(k, A, 150, 0, 0, "p");
        var reader = new FakeBandReader((_, _) => 4);

        Assert.Contains("unsupported_grid", await RunAsync(k, reader, A, "S2C_35RQQ_X"), StringComparison.Ordinal);
        Assert.Equal("failed:scene_not_found", await RunAsync(k, reader, A, "NO_SUCH_SCENE"));
        Assert.Empty(await k.Store.ParcelObservations.FindAsync(A, _ => true));
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public async Task APlannerQueuesOneJobPerUsableSceneInThePeriod()
    {
        var k = await NewAsync();
        var t = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Scene S(string id, DateTime when, double cloud = 1, int epsg = 32636) { var s = Tile36RTV(id, epsg); s.SensingTime = when; s.CloudCover = cloud; return s; }
        await k.Scenes.UpsertAsync([S("S2C_A", t), S("S2C_B", t.AddDays(5)), S("S2C_CLOUDY", t.AddDays(6), 80), S("S2C_ZONE35", t.AddDays(7), 1, 32635), S("S2C_OLD", t.AddDays(-100))]);
        var planner = new IngestPlanner(k.Scenes, k.Data.Jobs, k.Audit, k.Clock);
        var from = t.AddDays(-5);
        var to = t.AddDays(30);

        var ids = await planner.PlanAsync(A, "u1", from, to, maxCloudCover: 20);

        Assert.Equal(2, ids.Count);
        var jobs = await k.Data.Jobs.ListAsync(A, IngestPlanner.JobType);
        Assert.Equal(["S2C_A", "S2C_B"], jobs.Select(j => System.Text.Json.JsonSerializer.Deserialize<IngestPayload>(j.PayloadJson, JobJson.Options)!.SceneId).Order());
        Assert.Equal(["no_scenes"], (await Assert.ThrowsAsync<ValidationFailedException>(() => planner.PlanAsync(B, "u", from.AddYears(-3), from.AddYears(-3).AddDays(20)))).Codes);
        Assert.Equal(["invalid_period"], (await Assert.ThrowsAsync<ValidationFailedException>(() => planner.PlanAsync(A, "u", to, from))).Codes);
    }
}
