using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>What the Satellite screen reads: whether an organisation's own searches and readings are running, and which catalogue scenes cover its parcels.</summary>
[Collection(MongoCollection.Name)]
public class ImageryStatusTests
{
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));

    private sealed record Org(string Id, string Token);

    private static async Task<Org> OrgAsync(WebHarness web, string email, string name, string role = "org-admin", string? orgId = null)
    {
        string id, invite;
        if (orgId is null) (id, invite) = await web.OrgWithInvitationAsync(email, role, name);
        else { id = orgId; invite = await web.Service<InvitationService>().InviteAsync(TenantScope.ForTenant(orgId), "system", email, [role]); }
        return new Org(id, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    private static Scene Scene(string id, double minLon, double minLat, double maxLon, double maxLat, DateTime time, string tile = "36RTV") => new()
    {
        SceneId = id, Source = "fake", Collection = "sentinel-2-l2a", Platform = "sentinel-2c", SensingTime = time, Tile = tile, Epsg = 32636, CloudCover = 5,
        ProcessingBaseline = "05.13", BoaOffsetApplied = true, ItemSha256 = new string('A', 64), Attribution = "Contains modified Copernicus Sentinel data 2026",
        MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat, Assets = new() { ["blue"] = "b", ["green"] = "g", ["red"] = "r", ["nir"] = "n", ["scl"] = "s" },
    };

    private static async Task<Parcel> ParcelAsync(WebHarness web, string orgId, string label)
    {
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F " + label));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(150, 303800, 3409200), new BoundaryProvenance("survey", "s", Captured, 1));
        return parcel;
    }

    private static async Task ObserveAsync(WebHarness web, string orgId, Parcel parcel, string sceneId, bool readable)
    {
        var scope = TenantScope.ForTenant(orgId);
        var boundary = (await web.Service<BoundaryService>().GetCurrentAsync(scope, parcel.Id))!;
        await web.Service<KernelStore>().ParcelObservations.InsertAsync(scope, new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = boundary.Id, SceneId = sceneId, SensingTime = DateTime.UtcNow, FullPixels = 200, Readable = readable, Reason = readable ? null : ObservationReasons.SceneLost, IngestedAt = DateTime.UtcNow,
        });
    }

    private static string Url(string orgId, string suffix, DateTime? from = null, DateTime? to = null) =>
        $"/api/organisations/{orgId}/imagery/{suffix}?from={Uri.EscapeDataString((from ?? From).ToString("O"))}&to={Uri.EscapeDataString((to ?? To).ToString("O"))}";

    [Fact]
    public async Task StatusCountsOnlyTheOrganisationsOwnJobsByKindAndState()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var viewer = await OrgAsync(web, "viewer@delta.example", "x", "viewer", org.Id);
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        var jobs = web.Service<DataStore>().Jobs;
        var now = DateTime.UtcNow;
        await jobs.EnqueueAsync(TenantScope.ForTenant(org.Id), "imagery.discover", "{}", "u", now);
        await jobs.EnqueueAsync(TenantScope.ForTenant(org.Id), "imagery.discover", "{}", "u", now);
        await jobs.EnqueueAsync(TenantScope.ForTenant(org.Id), "imagery.chip", "{}", "u", now);
        await jobs.EnqueueAsync(TenantScope.ForTenant(other.Id), "imagery.ingest", "{}", "u", now);
        await jobs.EnqueueAsync(TenantScope.ForTenant(org.Id), "reports.freeze", "{}", "u", now);

        var mine = await Json(await Get(web, $"/api/organisations/{org.Id}/imagery/status", viewer.Token));

        Assert.Equal(2, mine.GetProperty("discover").GetProperty("queued").GetInt32());
        Assert.Equal(0, mine.GetProperty("ingest").GetProperty("queued").GetInt32());
        Assert.Equal(1, mine.GetProperty("chip").GetProperty("queued").GetInt32());
        var theirs = await Json(await Get(web, $"/api/organisations/{other.Id}/imagery/status", other.Token));
        Assert.Equal(1, theirs.GetProperty("ingest").GetProperty("queued").GetInt32());
        Assert.Equal(0, theirs.GetProperty("discover").GetProperty("queued").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{org.Id}/imagery/status", other.Token)).StatusCode);
    }

    [Fact]
    public async Task ScenesAreTheOnesOverTheOrganisationsParcelsWithTheirCounts()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1");
        var second = await ParcelAsync(web, org.Id, "P-2");
        var catalog = web.Service<SceneCatalog>();
        await catalog.UpsertAsync([
            Scene("OVER_1", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc)),
            Scene("OVER_2", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc)),
            Scene("ELSEWHERE", 32.0, 24.0, 33.0, 25.0, new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc), "36RWL"),
            Scene("TOO_EARLY", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc)),
        ]);
        await ObserveAsync(web, org.Id, parcel, "OVER_1", readable: true);
        await ObserveAsync(web, org.Id, second, "OVER_1", readable: false);

        var body = await Json(await Get(web, Url(org.Id, "scenes"), org.Token));

        Assert.True(body.GetProperty("hasParcels").GetBoolean());
        var scenes = body.GetProperty("scenes").EnumerateArray().ToList();
        Assert.Equal(["OVER_2", "OVER_1"], scenes.Select(s => s.GetProperty("sceneId").GetString()!).ToArray());
        var one = scenes[1];
        Assert.Equal((2, 2, 1), (one.GetProperty("parcelsUnder").GetInt32(), one.GetProperty("observed").GetInt32(), one.GetProperty("readable").GetInt32()));
        var two = scenes[0];
        Assert.Equal((2, 0, 0), (two.GetProperty("parcelsUnder").GetInt32(), two.GetProperty("observed").GetInt32(), two.GetProperty("readable").GetInt32()));
        Assert.Equal("Contains modified Copernicus Sentinel data 2026", one.GetProperty("attribution").GetString());
    }

    [Fact]
    public async Task AnotherOrganisationsObservationsNeverCountAndAnOrganisationWithNoParcelsSeesTheCatalogue()
    {
        await using var web = await WebHarness.StartAsync();
        var mine = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var empty = await OrgAsync(web, "admin@zr3i.example", "Platform Co");
        var parcel = await ParcelAsync(web, mine.Id, "P-1");
        await web.Service<SceneCatalog>().UpsertAsync([Scene("OVER_1", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc))]);
        await ObserveAsync(web, mine.Id, parcel, "OVER_1", readable: true);

        var theirs = await Json(await Get(web, Url(empty.Id, "scenes"), empty.Token));

        Assert.False(theirs.GetProperty("hasParcels").GetBoolean());
        var row = theirs.GetProperty("scenes").EnumerateArray().Single();
        Assert.Equal("OVER_1", row.GetProperty("sceneId").GetString());
        Assert.Equal((0, 0, 0), (row.GetProperty("parcelsUnder").GetInt32(), row.GetProperty("observed").GetInt32(), row.GetProperty("readable").GetInt32()));
    }

    [Theory]
    [InlineData("2026-11-30T00:00:00Z", "2026-10-01T00:00:00Z")]
    [InlineData("2025-01-01T00:00:00Z", "2026-11-30T00:00:00Z")]
    public async Task AnInvalidPeriodIsRefused(string from, string to)
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");

        var response = await Get(web, $"/api/organisations/{org.Id}/imagery/scenes?from={from}&to={to}", org.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_period", (await Json(response)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, $"/api/organisations/{org.Id}/imagery/scenes", org.Token)).StatusCode);
    }

    // ---- one scene, parcel by parcel ----

    private static async Task<Parcel> ParcelAtAsync(WebHarness web, string orgId, string label, double east, string farm = "Farm")
    {
        var scope = TenantScope.ForTenant(orgId);
        var f = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm(farm));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(f.Id, label));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(150, 303800 + east, 3409200), new BoundaryProvenance("survey", "s", Captured, 1));
        return parcel;
    }

    private static async Task ReadAsync(WebHarness web, string orgId, Parcel parcel, string sceneId, bool readable, string? reason = null, double? ndvi = null, int usable = 160)
    {
        var scope = TenantScope.ForTenant(orgId);
        var boundary = (await web.Service<BoundaryService>().GetCurrentAsync(scope, parcel.Id))!;
        await web.Service<KernelStore>().ParcelObservations.InsertAsync(scope, new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = boundary.Id, SceneId = sceneId, SensingTime = DateTime.UtcNow, FullPixels = 200, UsablePixels = usable, LostShare = 1 - usable / 200.0,
            Readable = readable, Reason = reason, NdviMedian = ndvi, IngestedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task ASceneListsEveryParcelUnderItByLabelWithItsStatusAndNumbers()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        var viewer = await OrgAsync(web, "viewer@delta.example", "x", "viewer", org.Id);
        var ok = await ParcelAtAsync(web, org.Id, "P-C", 0, "Farm One");
        var lost = await ParcelAtAsync(web, org.Id, "P-A", 300);
        var tiny = await ParcelAtAsync(web, org.Id, "P-D", 600);
        var unread = await ParcelAtAsync(web, org.Id, "P-B", 900);
        var theirs = await ParcelAtAsync(web, other.Id, "P-THEIRS", 0);
        await web.Service<SceneCatalog>().UpsertAsync([
            Scene("OVER_1", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc)),
            Scene("OVER_2", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc))]);
        // A reading of a different scene must never make a parcel look read in this one.
        await ReadAsync(web, org.Id, unread, "OVER_2", true, ndvi: 0.77);
        await ReadAsync(web, org.Id, ok, "OVER_1", true, ndvi: 0.31, usable: 150);
        await ReadAsync(web, org.Id, lost, "OVER_1", false, ObservationReasons.SceneLost, usable: 0);
        await ReadAsync(web, org.Id, tiny, "OVER_1", false, ObservationReasons.BelowResolution, usable: 0);
        await ReadAsync(web, other.Id, theirs, "OVER_1", true, ndvi: 0.9);

        var body = await Json(await Get(web, $"/api/organisations/{org.Id}/imagery/scenes/OVER_1", viewer.Token));

        Assert.Equal("OVER_1", body.GetProperty("scene").GetProperty("sceneId").GetString());
        var rows = body.GetProperty("parcels").EnumerateArray().ToList();
        Assert.Equal(["P-A", "P-B", "P-C", "P-D"], rows.Select(r => r.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(["scene_lost", "not_read", "usable", "below_resolution"], rows.Select(r => r.GetProperty("status").GetString()!).ToArray());
        var good = rows[2];
        Assert.Equal(("Farm One", 150, 200, 0.31), (good.GetProperty("farmName").GetString(), good.GetProperty("usablePixels").GetInt32(), good.GetProperty("fullPixels").GetInt32(), good.GetProperty("ndviMedian").GetDouble()));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("usablePixels").ValueKind);
        Assert.DoesNotContain("P-THEIRS", body.ToString());
        Assert.DoesNotContain("0.9", body.GetProperty("parcels").ToString());
    }

    [Fact]
    public async Task AParcelReadAgainstAnOlderBoundaryCountsAsNotReadAndPicturesAreListedWithTheirKind()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var scope = TenantScope.ForTenant(org.Id);
        var p = await ParcelAtAsync(web, org.Id, "P-1", 0);
        await web.Service<SceneCatalog>().UpsertAsync([Scene("OVER_1", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc))]);
        await ReadAsync(web, org.Id, p, "OVER_1", true, ndvi: 0.3);
        var current = (await web.Service<BoundaryService>().GetCurrentAsync(scope, p.Id))!;
        foreach (var kind in new[] { "true_colour", "ndvi" })
            await web.Service<KernelStore>().ParcelChips.InsertAsync(scope, new Zr3i.Kernel.Chips.ParcelChip { ParcelId = p.Id, BoundaryId = current.Id, SceneId = "OVER_1", Kind = kind, Path = "x", Sha256 = "AB", CreatedAt = DateTime.UtcNow });

        var before = (await Json(await Get(web, $"/api/organisations/{org.Id}/imagery/scenes/OVER_1", org.Token))).GetProperty("parcels")[0];
        Assert.Equal("usable", before.GetProperty("status").GetString());
        Assert.Equal(["ndvi", "true_colour"], before.GetProperty("chips").EnumerateArray().Select(c => c.GetProperty("kind").GetString()!).ToArray());

        // The boundary is redrawn: the old reading and pictures no longer describe the parcel.
        await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(200, 303800, 3409200), new BoundaryProvenance("survey", "s2", Captured.AddDays(1), 1));
        var after = (await Json(await Get(web, $"/api/organisations/{org.Id}/imagery/scenes/OVER_1", org.Token))).GetProperty("parcels")[0];
        Assert.Equal("not_read", after.GetProperty("status").GetString());
        Assert.Equal(0, after.GetProperty("chips").GetArrayLength());
    }

    [Fact]
    public async Task AnUnknownSceneIsNotFoundAndAnotherOrganisationCannotAskThroughThisOne()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        await web.Service<SceneCatalog>().UpsertAsync([Scene("OVER_1", 30.0, 30.5, 31.2, 31.5, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc))]);

        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{org.Id}/imagery/scenes/NOPE", org.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{org.Id}/imagery/scenes/OVER_1", other.Token)).StatusCode);
        var empty = await Json(await Get(web, $"/api/organisations/{other.Id}/imagery/scenes/OVER_1", other.Token));
        Assert.Equal(0, empty.GetProperty("parcels").GetArrayLength());
    }
}
