using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ImageryApiTests
{
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task OnlyPeopleWhoMayWriteCanQueueSearchesAndTheyNeedParcelsWithBoundaries()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var viewerInvite = await web.Service<Zr3i.Kernel.Identity.InvitationService>().InviteAsync(TenantScope.ForTenant(orgId), "system", "viewer@delta.example", ["viewer"]);
        var viewer = await web.SignInAsync("viewer@delta.example", await web.AcceptAsync(viewerInvite));
        var url = $"/api/organisations/{orgId}/imagery/discover";
        var body = new { from = "2026-09-01T00:00:00Z", to = "2026-11-01T00:00:00Z", maxCloudCover = 20 };

        var empty = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, body));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("nothing_to_search", (await Json(empty)).GetProperty("error").GetString());

        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F"));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "P"));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url, viewer, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync(url, body)).StatusCode);
        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, new { from = "2026-11-01T00:00:00Z", to = "2026-09-01T00:00:00Z" }));
        Assert.Equal("invalid_period", (await Json(bad)).GetProperty("error").GetString());

        var ok = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, body));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal(1, (await Json(ok)).GetProperty("searches").GetInt32());
        Assert.Single(await web.Service<DataStore>().Jobs.ListAsync(scope, DiscoveryPlanner.JobType));
    }

    [Fact]
    public async Task TheSceneCatalogueIsReadableByAnyoneSignedInInAStableOrder()
    {
        await using var web = await WebHarness.StartAsync();
        var (_, invite) = await web.OrgWithInvitationAsync("viewer@delta.example", "viewer");
        var user = await web.SignInAsync("viewer@delta.example", await web.AcceptAsync(invite));
        var t = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        Scene S(string id, DateTime when) => new()
        {
            SceneId = id, Source = "fake", Collection = "c", Platform = "sentinel-2c", SensingTime = when, Tile = "36RTV", Epsg = 32636, CloudCover = 2, ProcessingBaseline = "05.13",
            ItemSha256 = new string('A', 64), Attribution = "Contains modified Copernicus Sentinel data 2026", Assets = new() { ["red"] = "r" },
        };
        await web.Service<SceneCatalog>().UpsertAsync([S("S2C_B", t.AddDays(2)), S("S2C_A", t), S("S2C_OLD", t.AddDays(-200))]);

        var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/imagery/scenes?from=2026-09-01T00:00:00Z&to=2026-11-01T00:00:00Z", user)));

        Assert.Equal(["S2C_A", "S2C_B"], list.EnumerateArray().Select(s => s.GetProperty("sceneId").GetString()));
        Assert.Equal("Contains modified Copernicus Sentinel data 2026", list[0].GetProperty("attribution").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync("/api/imagery/scenes?from=2026-09-01&to=2026-11-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/imagery/scenes", user))).StatusCode);
    }
}
