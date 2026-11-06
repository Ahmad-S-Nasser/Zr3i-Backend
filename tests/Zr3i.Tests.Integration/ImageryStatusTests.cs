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
}
