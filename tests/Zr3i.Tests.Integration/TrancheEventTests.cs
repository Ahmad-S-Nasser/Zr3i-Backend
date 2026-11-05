using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Kernel.Imagery;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Tranches (a bank's own groups of parcels) and event windows (an insurer's own date ranges): both read only the organisation's own data.</summary>
[Collection(MongoCollection.Name)]
public class TrancheEventTests
{
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<JsonElement> Get(WebHarness web, string url, string token) => await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, token)));
    private static Task<HttpResponseMessage> Post(WebHarness web, string url, string token, object body) => web.Http.SendAsync(web.Request(HttpMethod.Post, url, token, body));

    private sealed record Party(string OrgId, string Token);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName, string type = "bank", string role = "org-admin")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, role, orgName, type);
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    private sealed record Portfolio(Party Org, Party Other, Farm FarmA, Farm FarmB, Parcel[] Parcels, Parcel OtherParcel);

    /// <summary>A bank with two farms and four parcels (external ids X-1..X-4), plus another organisation's parcel that must never be reachable.</summary>
    private static async Task<Portfolio> SeedAsync(WebHarness web)
    {
        var org = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank");
        var other = await PartyAsync(web, "admin@other-bank.example", "Other Bank");
        var scope = TenantScope.ForTenant(org.OrgId);
        var farms = web.Service<FarmService>();
        var store = web.Service<KernelStore>();
        var a = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Farm A"));
        var b = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Farm B"));

        async Task<Parcel> Make(Farm f, string label, string ext, string? status)
        {
            var p = await farms.CreateParcelAsync(scope, "seed", new NewParcel(f.Id, label, ExternalId: ext));
            await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));
            var ps = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(p.Id, Season, "wheat", 5.0, "2026-10-01", "contract"));
            if (status is not null)
                await store.Findings.InsertAsync(scope, new Finding
                {
                    ParcelId = p.Id, ParcelSeasonId = ps.Id, Season = Season, Slot = FindingSlots.Area, Type = "area_gap", Status = status, GapPercent = status == "FLAGGED" ? 30 : 1, NoisePercent = 5.3,
                    InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
                });
            return p;
        }

        var parcels = new[]
        {
            await Make(a, "P-1", "X-1", "MEASURED"),
            await Make(a, "P-2", "X-2", "FLAGGED"),
            await Make(b, "P-3", "X-3", "PENDING"),
            await Make(b, "P-4", "X-4", null),
        };
        var otherFarm = await farms.CreateFarmAsync(TenantScope.ForTenant(other.OrgId), "seed", new NewFarm("Their farm"));
        var theirs = await farms.CreateParcelAsync(TenantScope.ForTenant(other.OrgId), "seed", new NewParcel(otherFarm.Id, "T-1", ExternalId: "X-1"));
        return new Portfolio(org, other, a, b, parcels, theirs);
    }

    [Fact]
    public async Task ATrancheGroupsTheOrganisationsOwnParcelsByFarmOrExternalIdAndIsAudited()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var url = $"/api/organisations/{s.Org.OrgId}/tranches";

        var byFarm = await Post(web, url, s.Org.Token, new { name = "TR-01", season = Season, scheduledDate = "2026-12-01", farmIds = new[] { s.FarmA.Id } });
        Assert.Equal(HttpStatusCode.Created, byFarm.StatusCode);
        Assert.Equal(2, (await Json(byFarm)).GetProperty("parcelCount").GetInt32());

        var byExternal = await Post(web, url, s.Org.Token, new { name = "TR-02", season = Season, parcelExternalIds = new[] { "X-3", "X-4" } });
        Assert.Equal(2, (await Json(byExternal)).GetProperty("parcelCount").GetInt32());

        var list = await Get(web, url, s.Org.Token);
        Assert.Equal(["TR-01", "TR-02"], list.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order());
        Assert.Equal("2026-12-01", list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "TR-01").GetProperty("scheduledDate").GetString());

        var audit = await web.Service<KernelStore>().Audit.FindAsync(TenantScope.ForTenant(s.Org.OrgId), e => e.Action == "tranche.created");
        Assert.Equal(2, audit.Count);

        // Another organisation never sees it, and its own external id X-1 is a different parcel.
        Assert.Equal(0, (await Get(web, $"/api/organisations/{s.Other.OrgId}/tranches", s.Other.Token)).GetArrayLength());
        var theirs = await Post(web, $"/api/organisations/{s.Other.OrgId}/tranches", s.Other.Token, new { name = "TR-01", season = Season, parcelExternalIds = new[] { "X-1" } });
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.Equal(s.OtherParcel.Id, (await web.Service<KernelStore>().Tranches.FindAsync(TenantScope.ForTenant(s.Other.OrgId), _ => true)).Single().ParcelIds.Single());
    }

    [Theory]
    [InlineData("no_name")]
    [InlineData("invalid_season")]
    [InlineData("invalid_date")]
    [InlineData("no_parcels")]
    [InlineData("unknown_parcel")]
    [InlineData("duplicate_name")]
    public async Task BadTranchesAreRefusedWithACodeAndNeverReachAnotherOrganisationsParcels(string code)
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var url = $"/api/organisations/{s.Org.OrgId}/tranches";
        if (code == "duplicate_name") Assert.Equal(HttpStatusCode.Created, (await Post(web, url, s.Org.Token, new { name = "TR-01", season = Season, farmIds = new[] { s.FarmA.Id } })).StatusCode);

        object body = code switch
        {
            "no_name" => new { name = " ", season = Season, farmIds = new[] { s.FarmA.Id } },
            "invalid_season" => new { name = "TR-X", season = "x", farmIds = new[] { s.FarmA.Id } },
            "invalid_date" => new { name = "TR-X", season = Season, scheduledDate = "31/12/2026", farmIds = new[] { s.FarmA.Id } },
            "no_parcels" => new { name = "TR-X", season = Season },
            "unknown_parcel" => new { name = "TR-X", season = Season, parcelIds = new[] { s.OtherParcel.Id } },          // another organisation's parcel
            _ => new { name = "TR-01", season = Season, farmIds = new[] { s.FarmA.Id } },
        };
        var r = await Post(web, url, s.Org.Token, body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains(code, (await Json(r)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task OnlyRolesWithWriteRightsCanMakeATrancheButAViewerCanRead()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var scope = TenantScope.ForTenant(s.Org.OrgId);
        var token = await web.Service<Zr3i.Kernel.Identity.InvitationService>().InviteAsync(scope, "system", "viewer@nile-bank.example", ["viewer"]);
        var viewer = await web.SignInAsync("viewer@nile-bank.example", await web.AcceptAsync(token));
        var url = $"/api/organisations/{s.Org.OrgId}/tranches";

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(web, url, viewer, new { name = "TR-V", season = Season, farmIds = new[] { s.FarmA.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, s.Other.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, viewer))).StatusCode);
    }

    [Fact]
    public async Task TheTrancheCheckCountsVerdictsAndListsTheParcelsThatNeedALookWithoutRanking()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var created = await Json(await Post(web, $"/api/organisations/{s.Org.OrgId}/tranches", s.Org.Token, new { name = "TR-ALL", season = Season, farmIds = new[] { s.FarmA.Id, s.FarmB.Id } }));
        var id = created.GetProperty("id").GetString();

        var check = await Get(web, $"/api/organisations/{s.Org.OrgId}/tranches/{id}/check", s.Org.Token);

        Assert.Equal(("TR-ALL", 4), (check.GetProperty("tranche").GetProperty("name").GetString(), check.GetProperty("tranche").GetProperty("parcelCount").GetInt32()));
        var v = check.GetProperty("verdicts");
        Assert.Equal((1, 1, 1, 0, 1), (v.GetProperty("MEASURED").GetInt32(), v.GetProperty("FLAGGED").GetInt32(), v.GetProperty("PENDING").GetInt32(), v.GetProperty("NOT_ASSESSABLE").GetInt32(), v.GetProperty("none").GetInt32()));
        var look = check.GetProperty("needsALook").EnumerateArray().Select(p => p.GetProperty("label").GetString()).ToList();
        Assert.Equal(["P-2", "P-3", "P-4"], look);                                                       // flagged, pending and no result; the confirmed parcel is not listed; by label, not by value
        Assert.Equal("NOT_ESTABLISHED", check.GetProperty("accuracy").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{s.Other.OrgId}/tranches/{id}/check", s.Other.Token))).StatusCode);   // not theirs
    }

    private static async Task Obs(WebHarness web, TenantScope scope, Parcel p, string scene, DateTime when, bool readable, double? ndvi) =>
        await web.Service<KernelStore>().ParcelObservations.InsertAsync(scope, new ParcelObservation
        {
            ParcelId = p.Id, BoundaryId = p.CurrentBoundaryId ?? "b", SceneId = scene, SensingTime = when, Readable = readable, FullPixels = 200, UsablePixels = readable ? 200 : 10,
            NdviMedian = ndvi, PixelStackSha256 = "A",
        });

    [Fact]
    public async Task EventEvidenceGivesTheNearestClearSceneBeforeAndAfterWithTheirNdviAndNeverAJudgement()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var scope = TenantScope.ForTenant(s.Org.OrgId);
        var from = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);

        // P-1: a clear scene before and after, and a nearer one before that lost too many pixels to cloud (not usable).
        await Obs(web, scope, s.Parcels[0], "B-old", from.AddDays(-20), true, 0.60);
        await Obs(web, scope, s.Parcels[0], "B-near", from.AddDays(-2), true, 0.71);
        await Obs(web, scope, s.Parcels[0], "B-cloud", from.AddDays(-1), false, 0.99);              // unusable although it carries a value: it must be ignored
        await Obs(web, scope, s.Parcels[0], "In-window", to.AddHours(10), true, 0.90);                 // on the last day of the window: it belongs to the event, not to "after"
        await Obs(web, scope, s.Parcels[0], "A-near", to.AddDays(2), true, 0.52);
        await Obs(web, scope, s.Parcels[0], "A-far", to.AddDays(15), true, 0.50);
        // P-2: nothing after the window.
        await Obs(web, scope, s.Parcels[1], "B2", from.AddDays(-3), true, 0.69);
        // P-3: no scenes at all. P-4 is not covered.

        var created = await Post(web, $"/api/organisations/{s.Org.OrgId}/event-windows", s.Org.Token, new { name = "Hail 3-9 Jun", season = Season, from = "2026-06-03", to = "2026-06-09", parcelIds = new[] { s.Parcels[0].Id, s.Parcels[1].Id, s.Parcels[2].Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetString();

        var ev = await Get(web, $"/api/organisations/{s.Org.OrgId}/event-windows/{id}/evidence", s.Org.Token);
        Assert.Equal((3, 1), (ev.GetProperty("coveredParcels").GetInt32(), ev.GetProperty("parcelsWithEvidence").GetInt32()));
        var rows = ev.GetProperty("parcels").EnumerateArray().ToDictionary(p => p.GetProperty("label").GetString()!);

        var p1 = rows["P-1"];
        Assert.Equal("compared", p1.GetProperty("status").GetString());
        Assert.Equal(0.71, p1.GetProperty("before").GetProperty("ndvi").GetDouble());                  // the nearest usable scene before, not the older one, not the cloudy one
        Assert.Equal(0.52, p1.GetProperty("after").GetProperty("ndvi").GetDouble());                   // the nearest after, not the later one
        Assert.Equal("B-near", p1.GetProperty("before").GetProperty("sceneId").GetString());
        Assert.Equal(-0.19, p1.GetProperty("ndviChange").GetDouble(), 6);
        Assert.False(p1.TryGetProperty("verdict", out _));                                              // numbers only: no "change observed" judgement

        Assert.Equal(("not_assessable", "no_clear_scene_after"), (rows["P-2"].GetProperty("status").GetString(), rows["P-2"].GetProperty("reason").GetString()));
        Assert.Equal("no_clear_scene_before", rows["P-3"].GetProperty("reason").GetString());
        Assert.DoesNotContain("P-4", rows.Keys);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(web, $"/api/organisations/{s.Org.OrgId}/event-windows", s.Org.Token, new { name = "Bad", season = Season, from = "2026-06-09", to = "2026-06-03", farmIds = new[] { s.FarmA.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{s.Other.OrgId}/event-windows/{id}/evidence", s.Other.Token))).StatusCode);
    }
}
