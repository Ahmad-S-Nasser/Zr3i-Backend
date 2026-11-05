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

/// <summary>What the Verification workspace reads: parcels filtered by verdict and search, one parcel, and the analysis counts.</summary>
[Collection(MongoCollection.Name)]
public class VerificationReadTests
{
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<JsonElement> Get(WebHarness web, string url, string token) => await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, token)));

    private sealed record Party(string OrgId, string Token);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName)
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, "org-admin", orgName, "company");
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    private sealed record Seeded(Party Org, Party Other, Parcel Flagged, Parcel Confirmed, Parcel Pending, Parcel Bare);

    private static async Task<Seeded> SeedAsync(WebHarness web)
    {
        var org = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        var other = await PartyAsync(web, "admin@other.example", "Other");
        var scope = TenantScope.ForTenant(org.OrgId);
        var farms = web.Service<FarmService>();
        var store = web.Service<KernelStore>();
        var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0404"));

        async Task<Parcel> Make(string label, string? status, double? gap = null)
        {
            var p = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
            await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));
            var ps = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(p.Id, Season, "wheat", 5.0, "2026-10-01", "contract"));
            if (status is not null)
                await store.Findings.InsertAsync(scope, new Finding
                {
                    ParcelId = p.Id, ParcelSeasonId = ps.Id, Season = Season, Slot = FindingSlots.Area, Type = "area_gap", Status = status, GapPercent = gap, NoisePercent = 5.3,
                    InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
                });
            return p;
        }

        var flagged = await Make("P-0147", "FLAGGED", 27.0);
        var confirmed = await Make("P-0148", "MEASURED", 2.0);
        var pending = await Make("P-0149", "PENDING");
        var bare = await Make("Q-0001", null);

        async Task Obs(Parcel p, string scene, DateTime when, bool readable) =>
            await store.ParcelObservations.InsertAsync(scope, new ParcelObservation
            {
                ParcelId = p.Id, BoundaryId = p.CurrentBoundaryId ?? "b", SceneId = scene, SensingTime = when, Readable = readable, FullPixels = 200, UsablePixels = readable ? 200 : 20,
                NdviMedian = readable ? 0.5 : null, PixelStackSha256 = "A",
            });
        await Obs(flagged, "S2-A", new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc), true);
        await Obs(confirmed, "S2-A", new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc), true);          // the same scene seen by a second parcel counts once
        await Obs(flagged, "S2-B", new DateTime(2026, 12, 18, 0, 0, 0, DateTimeKind.Utc), true);
        await Obs(flagged, "S2-C", new DateTime(2027, 1, 5, 0, 0, 0, DateTimeKind.Utc), true);
        await Obs(flagged, "S2-D", new DateTime(2027, 1, 9, 0, 0, 0, DateTimeKind.Utc), false);          // lost to cloud: not a usable scene
        return new Seeded(org, other, flagged, confirmed, pending, bare);
    }

    [Fact]
    public async Task TheParcelsListFiltersByVerdictAndSearchAndTheseNeverCrossOrganisations()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var url = $"/api/organisations/{s.Org.OrgId}/parcels?season={Season}";

        async Task<List<string>> Labels(string query) => (await Get(web, url + query, s.Org.Token)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!).Order().ToList();

        Assert.Equal(["P-0147", "P-0148", "P-0149", "Q-0001"], await Labels(""));
        Assert.Equal(["P-0147"], await Labels("&verdict=FLAGGED"));
        Assert.Equal(["P-0148"], await Labels("&verdict=MEASURED"));
        Assert.Equal(["Q-0001"], await Labels("&verdict=none"));                                          // no finding yet
        Assert.Equal(["P-0147", "P-0148", "P-0149"], await Labels("&q=p-01"));                            // case-insensitive search
        Assert.Equal(["P-0148"], await Labels("&q=0148&verdict=MEASURED"));
        Assert.Empty(await Labels("&q=0148&verdict=FLAGGED"));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "&verdict=APPROVED", s.Org.Token))).StatusCode);
        Assert.Equal(["P-0148"], (await Get(web, url + "&verdict=MEASURED&pageSize=1", s.Org.Token)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList());

        var other = await Get(web, $"/api/organisations/{s.Other.OrgId}/parcels?season={Season}&q=P-01", s.Other.Token);
        Assert.Equal(0, other.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task OneParcelCanBeReadWithItsSeasonAndCurrentFindingAndNotFoundOtherwise()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);

        var one = await Get(web, $"/api/organisations/{s.Org.OrgId}/parcels/{s.Flagged.Id}?season={Season}", s.Org.Token);
        Assert.Equal(("P-0147", "F-0404", 5.0, "FLAGGED"), (one.GetProperty("label").GetString(), one.GetProperty("farmName").GetString(), one.GetProperty("declaredAreaFeddan").GetDouble(), one.GetProperty("finding").GetProperty("status").GetString()));
        Assert.Equal(27.0, one.GetProperty("finding").GetProperty("gapPercent").GetDouble());

        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{s.Org.OrgId}/parcels/nope", s.Org.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{s.Other.OrgId}/parcels/{s.Flagged.Id}", s.Other.Token))).StatusCode);   // another organisation's parcel
    }

    [Fact]
    public async Task TheAnalysisCountsVerdictsAndUsableScenesPerMonthWithoutRanking()
    {
        await using var web = await WebHarness.StartAsync();
        var s = await SeedAsync(web);
        var url = $"/api/organisations/{s.Org.OrgId}/verification/analysis?season={Season}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, s.Other.Token))).StatusCode);

        var a = await Get(web, url, s.Org.Token);
        Assert.Equal(4, a.GetProperty("parcels").GetInt32());
        var v = a.GetProperty("verdicts");
        Assert.Equal((1, 1, 1, 0, 1), (v.GetProperty("MEASURED").GetInt32(), v.GetProperty("FLAGGED").GetInt32(), v.GetProperty("PENDING").GetInt32(), v.GetProperty("NOT_ASSESSABLE").GetInt32(), v.GetProperty("none").GetInt32()));
        var months = a.GetProperty("scenesPerMonth").EnumerateArray().ToDictionary(m => m.GetProperty("month").GetString()!, m => m.GetProperty("scenes").GetInt32());
        Assert.Equal((2, 1), (months["2026-12"], months["2027-01"]));                                       // S2-A once (two parcels), S2-B; S2-C only (S2-D was lost to cloud)
        Assert.Equal("NOT_ESTABLISHED", a.GetProperty("accuracy").GetString());
    }
}
