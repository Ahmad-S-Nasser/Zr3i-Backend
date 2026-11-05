using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Indicators;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class FarmListTests(MongoFixture mongo)
{
    private readonly MongoFixture _mongo = mongo;
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Parcel> ParcelAsync(WebHarness web, TenantScope scope, Farm farm, string label, double? side)
    {
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
        if (side is not null)
            await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(side.Value), new BoundaryProvenance("survey", "s", Captured, 1));
        return parcel;
    }

    [Fact]
    public async Task FarmsArePagedWithTheirParcelCountAndMeasuredAreaAndAreNeverRankedByRisk()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var (otherOrg, otherInvite) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other");
        var outsider = await web.SignInAsync("admin@other.example", await web.AcceptAsync(otherInvite));
        var scope = TenantScope.ForTenant(orgId);
        var farms = web.Service<FarmService>();

        var f1 = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0401", Governorate: "Kafr El Sheikh"));
        var f2 = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0402"));
        var f3 = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0403"));
        var p1 = await ParcelAsync(web, scope, f1, "a", 100);
        var p2 = await ParcelAsync(web, scope, f1, "b", 200);
        await ParcelAsync(web, scope, f1, "c-no-boundary", null);
        await ParcelAsync(web, scope, f2, "d", 150);
        await web.Service<FarmService>().CreateFarmAsync(TenantScope.ForTenant(otherOrg), "seed", new NewFarm("Other farm"));

        var url = $"/api/organisations/{orgId}/farms";
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, outsider))).StatusCode);

        var first = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "?pageSize=2", admin)));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var second = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url + $"?pageSize=2&cursor={Uri.EscapeDataString(cursor!)}", admin)));
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var all = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(["F-0401", "F-0402", "F-0403"], all.Keys.Order());                                          // only this organisation's farms
        var a = all["F-0401"];
        Assert.Equal((3, "Kafr El Sheikh"), (a.GetProperty("parcelCount").GetInt32(), a.GetProperty("governorate").GetString()));
        Assert.Equal((100.0 * 100 + 200.0 * 200) / 4200.83, a.GetProperty("areaFeddan").GetDouble(), 2);        // parcels without a boundary add no area
        Assert.Equal(0, all["F-0403"].GetProperty("parcelCount").GetInt32());
        Assert.Equal(0.0, all["F-0403"].GetProperty("areaFeddan").GetDouble());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("fri").ValueKind);                                          // no season asked for
        Assert.Equal(f1.Id, a.GetProperty("id").GetString());
        _ = (f2, f3, p1, p2);
    }

    [Fact]
    public async Task ASeasonAddsEachFarmsCurrentFriWithoutABand()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F-0404"));
        var bare = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F-0405"));
        var parcel = await ParcelAsync(web, scope, farm, "p", 100);
        var ps = await web.Service<FarmService>().DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, Season, "wheat", 2.0, "2026-10-01", "contract"));
        var recorder = web.Service<IndicatorRecorder>();
        await recorder.RecordAsync(scope, "seed", new IndicatorDraft("CRI", "parcel_season", ps.Id, "FRI-2026-01", 1, [new("water_security", 100, 40, true)], 40, 0.9));
        var fri = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgId}/risk/farms/{farm.Id}/seasons/{Season}/fri", admin, new { adjustments = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.OK, fri.StatusCode);

        var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/farms?season={Season}", admin)));
        var rows = list.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);

        Assert.Equal(40.0, rows["F-0404"].GetProperty("fri").GetProperty("value").GetDouble(), 9);
        Assert.Equal("specified_not_calibrated", rows["F-0404"].GetProperty("fri").GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, rows["F-0405"].GetProperty("fri").ValueKind);                            // a farm with no FRI says so
        Assert.False(rows["F-0404"].GetProperty("fri").TryGetProperty("band", out _));
        _ = bare;

        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/farms?season=x", admin));
        Assert.Contains("invalid_season", await bad.Content.ReadAsStringAsync());
        var tooBig = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/farms?pageSize=5000", admin));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
    }
}
