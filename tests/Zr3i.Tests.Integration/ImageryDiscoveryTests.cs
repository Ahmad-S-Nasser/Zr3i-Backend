using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Imagery;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ImageryDiscoveryTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("imagery-a");
    private static readonly TenantScope B = TenantScope.ForTenant("imagery-b");
    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return k;
    }

    private static Scene SceneOf(string id, DateTime sensed, string hash = "H1", string tile = "36RTV") => new()
    {
        SceneId = id, Source = "fake", Collection = "sentinel-2-l2a", Platform = "sentinel-2c", SensingTime = sensed, Tile = tile, Epsg = 32636,
        CloudCover = 1.5, ProcessingBaseline = "05.13", BoaOffsetApplied = true, ItemSha256 = hash.PadRight(64, '0'), Attribution = "Contains modified Copernicus Sentinel data 2026",
        Assets = new() { ["blue"] = "b", ["green"] = "g", ["red"] = "r", ["nir"] = "n", ["scl"] = "s" },
    };

    private sealed class FakeSource(Func<SceneQuery, SceneSearchResult> answer) : ISceneSource
    {
        public List<SceneQuery> Queries { get; } = [];
        public string Name => "fake";

        public Task<SceneSearchResult> SearchAsync(SceneQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }

    private static async Task<Parcel> ParcelAtAsync(KernelHarness k, TenantScope scope, double eastingOffset, double northingOffset, string label)
    {
        var farm = await k.Farms.CreateFarmAsync(scope, "u", new NewFarm("F " + label));
        var parcel = await k.Farms.CreateParcelAsync(scope, "u", new NewParcel(farm.Id, label));
        await k.Boundaries.AddAsync(scope, "u", parcel.Id, KernelHarness.SquareMetres(150, 303800 + eastingOffset, 3409200 + northingOffset),
            new BoundaryProvenance("survey", "s", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), 1));
        return parcel;
    }

    // ---- catalogue ----

    [Fact]
    public async Task TheCatalogueAddsNewScenesLeavesKnownOnesAloneAndNoticesAChangedItem()
    {
        var k = await NewAsync();
        var t = new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc);

        var first = await k.Scenes.UpsertAsync([SceneOf("S2C_A", t), SceneOf("S2C_B", t.AddDays(-3))]);
        var again = await k.Scenes.UpsertAsync([SceneOf("S2C_A", t), SceneOf("S2C_B", t.AddDays(-3))]);
        var changed = await k.Scenes.UpsertAsync([SceneOf("S2C_A", t, hash: "H2"), SceneOf("S2C_B", t.AddDays(-3))]);

        Assert.Equal((2, 0, 0), (first.Inserted, first.Updated, first.Unchanged));
        Assert.Equal((0, 0, 2), (again.Inserted, again.Updated, again.Unchanged));
        Assert.Equal((0, 1, 1), (changed.Inserted, changed.Updated, changed.Unchanged));
        Assert.Equal(2, await k.Store.Scenes.CountAsync(PlatformTenant.Scope, _ => true));
        Assert.Single(await k.Store.Audit.FindAsync(PlatformTenant.Scope, e => e.Action == "scene.item_changed"));
    }

    [Fact]
    public async Task ListingIsFilteredByPeriodAndTileAndAlwaysInTheSameOrder()
    {
        var k = await NewAsync();
        var t = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await k.Scenes.UpsertAsync([
            SceneOf("S2C_C", t.AddDays(10), tile: "36RUV"), SceneOf("S2C_A", t), SceneOf("S2C_B", t), SceneOf("S2C_OLD", t.AddDays(-90)), SceneOf("S2C_D", t.AddDays(5)),
        ]);

        var all = await k.Scenes.ListAsync(From, To);
        var tile = await k.Scenes.ListAsync(From, To, "36RUV");

        Assert.Equal(["S2C_A", "S2C_B", "S2C_D", "S2C_C"], all.Select(s => s.SceneId));        // by time, then id; the old one is outside the period
        Assert.Equal(["S2C_C"], tile.Select(s => s.SceneId));
    }

    [Fact]
    public async Task TheCatalogueIsSharedPublicDataAndLivesInThePlatformTenant()
    {
        var k = await NewAsync();
        await k.Scenes.UpsertAsync([SceneOf("S2C_X", From.AddDays(5))]);

        Assert.Equal(PlatformTenant.Id, (await k.Store.Scenes.FindAsync(PlatformTenant.Scope, _ => true)).Single().TenantId);
        Assert.Empty(await k.Store.Scenes.FindAsync(A, _ => true));      // not copied into any customer's tenant
    }

    // ---- planning ----

    [Fact]
    public async Task OneSearchIsQueuedForEachMapCellThatHoldsAParcelNotOneBoxOverEverything()
    {
        var k = await NewAsync();
        await ParcelAtAsync(k, A, 0, 0, "delta-1");
        await ParcelAtAsync(k, A, 200, 100, "delta-2");                 // same cell as the first
        await ParcelAtAsync(k, A, 0, 100_000, "far-north");             // about 0.9 degrees north: a different cell
        var farm = await k.Farms.CreateFarmAsync(A, "u", new NewFarm("No boundary"));
        await k.Farms.CreateParcelAsync(A, "u", new NewParcel(farm.Id, "pending"));      // no boundary: nothing to search for
        await ParcelAtAsync(k, B, 0, 80_000, "other tenant's parcel");   // must not add a search for A

        var jobIds = await k.Discovery.PlanAsync(A, "u1", From, To, maxCloudCover: 20);

        Assert.Equal(2, jobIds.Count);
        var jobs = await k.Data.Jobs.ListAsync(A, DiscoveryPlanner.JobType);
        Assert.Equal(2, jobs.Count);
        var payloads = jobs.Select(j => System.Text.Json.JsonSerializer.Deserialize<DiscoverPayload>(j.PayloadJson, JobJson.Options)!).OrderBy(p => p.MinLat).ToList();
        Assert.All(payloads, p => Assert.Equal((0.25, 0.25, 20.0), (p.MaxLon - p.MinLon, p.MaxLat - p.MinLat, p.MaxCloudCover)));
        Assert.Equal((30.75, 31.0), (payloads[0].MinLon, payloads[0].MaxLon));     // the quarter-degree cell that holds the parcels near 30.95 E
        Assert.InRange(payloads[1].MinLat - payloads[0].MinLat, 0.5, 1.0);        // a different cell, about 0.75 degrees further north
        Assert.Empty(await k.Data.Jobs.ListAsync(B));
    }

    [Fact]
    public async Task PlanningRefusesNonsenseAndAnEmptyBook()
    {
        var k = await NewAsync();

        Assert.Equal(["nothing_to_search"], (await Assert.ThrowsAsync<ValidationFailedException>(() => k.Discovery.PlanAsync(A, "u", From, To))).Codes);
        await ParcelAtAsync(k, A, 0, 0, "p");
        Assert.Equal(["invalid_period"], (await Assert.ThrowsAsync<ValidationFailedException>(() => k.Discovery.PlanAsync(A, "u", To, From))).Codes);
        Assert.Equal(["invalid_period"], (await Assert.ThrowsAsync<ValidationFailedException>(() => k.Discovery.PlanAsync(A, "u", From, From.AddDays(401)))).Codes);
        Assert.Equal(["invalid_cloud_limit"], (await Assert.ThrowsAsync<ValidationFailedException>(() => k.Discovery.PlanAsync(A, "u", From, To, 101))).Codes);
        Assert.Empty(await k.Data.Jobs.ListAsync(A));
    }

    // ---- the job ----

    private JobRunner RunnerFor(KernelHarness k, ISceneSource source) =>
        new(k.Data.CreateJobClaimer(new JobQueueOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.Zero)), [new DiscoverHandler(source, k.Scenes)], k.Clock, "imagery-worker");

    [Fact]
    public async Task TheJobSearchesTheProviderAndFillsTheCatalogueAndRunningItAgainAddsNothing()
    {
        var k = await NewAsync();
        await ParcelAtAsync(k, A, 0, 0, "p");
        var t = new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc);
        var source = new FakeSource(_ => new SceneSearchResult([SceneOf("S2C_A", t), SceneOf("S2C_B", t.AddDays(-3))], Skipped: 1));

        await k.Discovery.PlanAsync(A, "u1", From, To, 30);
        await RunnerFor(k, source).RunUntilIdleAsync();
        await k.Discovery.PlanAsync(A, "u1", From, To, 30);
        await RunnerFor(k, source).RunUntilIdleAsync();

        var jobs = await k.Data.Jobs.ListAsync(A, DiscoveryPlanner.JobType);
        Assert.All(jobs, j => Assert.Equal("succeeded", j.Status));
        Assert.Contains("\"inserted\":2", jobs[0].ResultJson, StringComparison.Ordinal);
        Assert.Contains("\"skipped\":1", jobs[0].ResultJson, StringComparison.Ordinal);
        Assert.Contains("\"unchanged\":2", jobs[1].ResultJson, StringComparison.Ordinal);
        Assert.Equal(2, await k.Store.Scenes.CountAsync(PlatformTenant.Scope, _ => true));
        Assert.All(source.Queries, q => Assert.Equal((30.0, From, To), (q.MaxCloudCover, q.From, q.To)));
    }

    [Fact]
    public async Task AProviderThatIsDownIsRetriedLaterAndOneThatRejectsTheRequestFailsForGood()
    {
        var k = await NewAsync();
        await ParcelAtAsync(k, A, 0, 0, "p");
        await k.Discovery.PlanAsync(A, "u1", From, To);

        await RunnerFor(k, new FakeSource(_ => throw new StacException("down", 503))).RunOnceAsync();
        var retry = (await k.Data.Jobs.ListAsync(A, DiscoveryPlanner.JobType)).Single();
        Assert.Equal(("queued", "provider_unavailable"), (retry.Status, retry.ErrorCode));

        await RunnerFor(k, new FakeSource(_ => throw new StacException("bad request", 400))).RunUntilIdleAsync();
        var failed = (await k.Data.Jobs.ListAsync(A, DiscoveryPlanner.JobType)).Single();
        Assert.Equal(("failed", "provider_rejected"), (failed.Status, failed.ErrorCode));
        Assert.Equal(0, await k.Store.Scenes.CountAsync(PlatformTenant.Scope, _ => true));
    }
}
