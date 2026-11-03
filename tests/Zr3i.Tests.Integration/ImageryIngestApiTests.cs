using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Imagery;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ImageryIngestApiTests
{
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task QueueingIsPermissionCheckedAndAParcelsObservationsCarryTheirProvenanceAndStayInTheirOrganisation()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var (otherId, otherInvite) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other");
        var outsider = await web.SignInAsync("admin@other.example", await web.AcceptAsync(otherInvite));
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F"));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "P"));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));

        var url = $"/api/organisations/{orgId}/imagery/ingest";
        var body = new { from = "2026-10-01T00:00:00Z", to = "2026-11-01T00:00:00Z" };
        var none = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, body));
        Assert.Equal("no_scenes", (await Json(none)).GetProperty("error").GetString());

        await web.Service<SceneCatalog>().UpsertAsync([new Scene
        {
            SceneId = "S2C_36RTV_20261027_0_L2A", Source = "fake", Collection = "c", Platform = "sentinel-2c", SensingTime = new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc),
            Tile = "36RTV", Epsg = 32636, CloudCover = 1, ProcessingBaseline = "05.13", BoaOffsetApplied = true, ItemSha256 = new string('D', 64),
            Attribution = "Contains modified Copernicus Sentinel data 2026", MinLon = 30, MinLat = 30.5, MaxLon = 31.2, MaxLat = 31.5,
            Assets = new() { ["blue"] = "blue", ["green"] = "green", ["red"] = "red", ["nir"] = "nir", ["scl"] = "scl" },
        }]);

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url, outsider, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync(url, body)).StatusCode);
        var queued = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, body));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(1, (await Json(queued)).GetProperty("scenes").GetInt32());

        // Run the job the way the worker would, then read what it recorded.
        var reader = new FakeReader();
        var runner = new Zr3i.Kernel.Jobs.JobRunner(web.Service<DataStore>().CreateJobClaimer(), [new IngestHandler(web.Service<Zr3i.Kernel.KernelStore>(), reader, web.Service<Zr3i.Kernel.Methods.MethodRegistry>(), TimeProvider.System)], TimeProvider.System, "w");
        Assert.Equal(1, await runner.RunUntilIdleAsync());

        var obs = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/parcels/{parcel.Id}/observations", admin)));
        var o = obs.EnumerateArray().Single();
        Assert.Equal(("S2C_36RTV_20261027_0_L2A", true, "GEO-PIXEL-2026-01"), (o.GetProperty("sceneId").GetString(), o.GetProperty("readable").GetBoolean(), o.GetProperty("pixelMethodId").GetString()));
        Assert.Equal(("ZR3I-R0-2026-01", "draft"), (o.GetProperty("method").GetProperty("id").GetString(), o.GetProperty("method").GetProperty("state").GetString()));
        Assert.Matches("^[0-9A-F]{64}$", o.GetProperty("pixelStackSha256").GetString()!);
        Assert.Equal("Contains modified Copernicus Sentinel data 2026", o.GetProperty("attribution").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/parcels/{parcel.Id}/observations", outsider))).StatusCode);
        var wrongTenant = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{otherId}/parcels/{parcel.Id}/observations", outsider));
        Assert.Equal(HttpStatusCode.NotFound, wrongTenant.StatusCode);                  // another organisation's parcel looks like it does not exist
    }

    private sealed class FakeReader : IBandReader
    {
        public string Name => "fake 1.0";

        public Task<BandWindow> ReadAsync(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct = default)
        {
            var grid = new Zr3i.Geo.RasterGrid(199980, 3500040, href == "scl" ? 20 : 10);
            var w = grid.WindowOf(latticePixels);
            return Task.FromResult(new BandWindow([.. Enumerable.Repeat(href == "scl" ? 4 : 500, w.Cells)], w, grid));
        }
    }
}
