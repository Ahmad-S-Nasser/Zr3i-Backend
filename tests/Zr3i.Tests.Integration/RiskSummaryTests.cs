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
public class RiskSummaryTests(MongoFixture mongo)
{
    private readonly MongoFixture _mongo = mongo;
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Seasons = ["2021-22", "2022-23", "2023-24", "2024-25", "2025-26"];

    private static readonly (string Label, double Area, double Cri, string Crop)[] Delta =
    [
        ("ZR-KS-01", 62.4, 19.0, "wheat"), ("ZR-KS-02", 48.1, 26.0, "wheat"), ("ZR-KS-03", 55.7, 50.3, "maize"), ("ZR-KS-04", 71.2, 33.0, "beet"),
        ("ZR-KS-05", 39.8, 22.0, "wheat"), ("ZR-KS-06", 44.3, 58.0, "maize"), ("ZR-KS-07", 52.6, 71.0, "rice"), ("ZR-KS-08", 33.9, 29.0, "beet"), ("ZR-KS-09", 20.0, 41.0, "wheat"),
    ];

    private static readonly object[] DeltaAdjustments =
    [
        new { key = "crop_concentration", value = 1.2 }, new { key = "spatial_concentration", value = 3.1 }, new { key = "irrigation_diversity", value = 2.0 },
        new { key = "record_length", value = -5.8 }, new { key = "worst_parcel_exposure", value = 1.2 },
    ];

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Setup(WebHarness Web, string OrgId, string Admin, string Outsider, Farm Farm, TenantScope Scope);

    private static async Task<Setup> StartAsync()
    {
        var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var (_, otherInvite) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other");
        var outsider = await web.SignInAsync("admin@other.example", await web.AcceptAsync(otherInvite));
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("Delta Agro Estate"));
        return new Setup(web, orgId, admin, outsider, farm, scope);
    }

    private static async Task<ParcelSeason> ParcelSeasonAsync(Setup s, string label, double areaFeddan, string? crop = "wheat", bool boundary = true)
    {
        var farms = s.Web.Service<FarmService>();
        var parcel = await farms.CreateParcelAsync(s.Scope, "seed", new NewParcel(s.Farm.Id, label));
        if (boundary)
            await s.Web.Service<BoundaryService>().AddAsync(s.Scope, "seed", parcel.Id, KernelHarness.SquareMetres(Math.Sqrt(areaFeddan * 4200.83)), new BoundaryProvenance("survey", "s", Captured, 1));
        return await farms.DeclareSeasonAsync(s.Scope, "seed", new NewParcelSeason(parcel.Id, Season, crop, areaFeddan, "2026-10-01", "contract"));
    }

    private static Task SeedCriAsync(Setup s, string parcelSeasonId, double cri, bool insufficient = false, int closedSeasons = 8) =>
        s.Web.Service<IndicatorRecorder>().RecordAsync(s.Scope, "seed", new IndicatorDraft(
            "CRI", "parcel_season", parcelSeasonId, "FRI-2026-01", 1, [new("water_security", 100, cri, true)], cri, insufficient ? 0.5 : 0.9, insufficient, null, closedSeasons));

    private static string FarmUrl(Setup s, string tail) => $"/api/organisations/{s.OrgId}/risk/farms/{s.Farm.Id}/seasons/{Season}/{tail}";

    private static async Task SeedDeltaAsync(Setup s)
    {
        foreach (var (label, area, cri, crop) in Delta) await SeedCriAsync(s, (await ParcelSeasonAsync(s, label, area, crop)).Id, cri);
        var r = await s.Web.Http.SendAsync(s.Web.Request(HttpMethod.Post, FarmUrl(s, "fri"), s.Admin, new { adjustments = DeltaAdjustments }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task TheFarmSummaryGivesEverythingTheFarmBoardShowsInOneCall()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        await SeedDeltaAsync(set);

        var r = await web.Http.SendAsync(web.Request(HttpMethod.Get, FarmUrl(set, "summary"), set.Admin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = await Json(r);

        Assert.Equal(("Delta Agro Estate", Season, "specified", "specified_not_calibrated", "FRI-2026-01"),
            (b.GetProperty("farm").GetProperty("name").GetString(), b.GetProperty("season").GetString(), b.GetProperty("maturity").GetString(),
             b.GetProperty("label").GetString(), b.GetProperty("method").GetProperty("id").GetString()));
        Assert.Equal(40.4, Math.Round(b.GetProperty("fri").GetProperty("value").GetDouble(), 1));
        Assert.Equal(38.7, Math.Round(b.GetProperty("awcri").GetDouble(), 1));
        Assert.Equal(1.7, Math.Round(b.GetProperty("adjustmentsSum").GetDouble(), 1));
        Assert.Equal((9, 428.0, 8, false), (b.GetProperty("parcelCount").GetInt32(), Math.Round(b.GetProperty("totalAreaFeddan").GetDouble(), 1), b.GetProperty("seasonsOnFile").GetInt32(), b.GetProperty("friStale").GetBoolean()));
        Assert.Equal("farm_cohort_not_defined", b.GetProperty("fri").GetProperty("bandReason").GetString());

        var rows = b.GetProperty("parcels").EnumerateArray().ToList();
        Assert.Equal(Delta.Select(d => d.Label), rows.Select(x => x.GetProperty("label").GetString()));            // by id, never by value
        var rice = rows.Single(x => x.GetProperty("label").GetString() == "ZR-KS-07");
        Assert.Equal(("rice", 52.6, 71.0, 3734.6, "included"), (rice.GetProperty("crop").GetString(), Math.Round(rice.GetProperty("areaFeddan").GetDouble(), 1),
            rice.GetProperty("cri").GetDouble(), Math.Round(rice.GetProperty("areaTimesCri").GetDouble(), 1), rice.GetProperty("status").GetString()));
        Assert.Equal("no_reference_distribution", rice.GetProperty("bandReason").GetString());

        var exposure = b.GetProperty("exposure");
        Assert.Equal(("ZR-KS-07", 12.3, 34.2), (exposure.GetProperty("label").GetString(), Math.Round(exposure.GetProperty("areaSharePercent").GetDouble(), 1), Math.Round(exposure.GetProperty("awcriWithout").GetDouble(), 1)));
        Assert.False(exposure.TryGetProperty("friWithout", out _));                                                  // needs the open adjustment formulas
    }

    [Fact]
    public async Task ParcelsThatCannotBeRolledUpAreListedWithTheirReasonAndNeverCountedAsLowRisk()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var included = await ParcelSeasonAsync(set, "A-included", 10);
        var insufficient = await ParcelSeasonAsync(set, "B-insufficient", 20);
        var noCri = await ParcelSeasonAsync(set, "C-no-cri", 30);
        var noBoundary = await ParcelSeasonAsync(set, "D-no-boundary", 40, boundary: false);
        await SeedCriAsync(set, included.Id, 40);
        await SeedCriAsync(set, insufficient.Id, 90, insufficient: true);
        await SeedCriAsync(set, noBoundary.Id, 55);

        var b = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, FarmUrl(set, "summary"), set.Admin)));
        var rows = b.GetProperty("parcels").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!);

        Assert.Equal("included", rows["A-included"].GetProperty("status").GetString());
        Assert.Equal("insufficient", rows["B-insufficient"].GetProperty("status").GetString());
        Assert.Equal(90.0, rows["B-insufficient"].GetProperty("cri").GetDouble());
        Assert.Equal(JsonValueKind.Null, rows["B-insufficient"].GetProperty("areaTimesCri").ValueKind);
        Assert.Equal(("no_cri", JsonValueKind.Null), (rows["C-no-cri"].GetProperty("status").GetString(), rows["C-no-cri"].GetProperty("cri").ValueKind));
        Assert.Equal(("no_boundary", JsonValueKind.Null), (rows["D-no-boundary"].GetProperty("status").GetString(), rows["D-no-boundary"].GetProperty("areaFeddan").ValueKind));
        Assert.Equal(40.0, b.GetProperty("awcri").GetDouble(), 9);                                                    // only the included parcel counts
        Assert.Equal(JsonValueKind.Null, b.GetProperty("fri").ValueKind);                                             // nothing recorded yet
        Assert.False(b.GetProperty("friStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("exposure").ValueKind);                                        // one included parcel: nothing to remove
    }

    [Fact]
    public async Task ANewerParcelCriMakesTheRecordedFriStale()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        await SeedDeltaAsync(set);
        var ps = (await web.Service<KernelStore>().ParcelSeasons.FindAsync(set.Scope, p => p.Season == Season)).First();
        await SeedCriAsync(set, ps.Id, 99);

        var b = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, FarmUrl(set, "summary"), set.Admin)));
        Assert.True(b.GetProperty("friStale").GetBoolean());
    }

    [Fact]
    public async Task TheParcelSummaryGivesSubjectFeaturesContributionsProvenanceAndBandInOneCall()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "ZR-KS-03", 55.7, "maize");
        var cri = $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/cri";
        var features = new { ndmiMin = 24.0, heatStress = 0.58, ndviIntegral = 46.0, areaVariancePct = 12.0, ndviCohortPercentile = 34.0, closedSeasonYields = new[] { 73.5, 79, 97, 100, 100, 104, 108, 112 }, validFraction = 0.9 };
        await web.Http.SendAsync(web.Request(HttpMethod.Post, cri, set.Admin, features));
        var scores = Enumerable.Range(0, 100).Select(i => 30.0 + i * 0.5).ToArray();
        await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{set.OrgId}/risk/reference-distributions", set.Admin,
            new { kind = "parcel", cohortLevel = "crop_national", cohortKey = "maize", seasons = Seasons, scores }));

        var b = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/summary", set.Admin)));

        var subject = b.GetProperty("subject");
        Assert.Equal(("ZR-KS-03", "Delta Agro Estate", Season, "maize", 55.7), (subject.GetProperty("label").GetString(), subject.GetProperty("farmName").GetString(),
            subject.GetProperty("season").GetString(), subject.GetProperty("crop").GetString(), Math.Round(subject.GetProperty("areaFeddan").GetDouble(), 1)));
        Assert.Equal(set.Farm.Id, subject.GetProperty("farmId").GetString());

        var cur = b.GetProperty("current");
        Assert.Equal(50.3, Math.Round(cur.GetProperty("value").GetDouble(), 1));
        Assert.Equal((8, 100.0), (cur.GetProperty("closedSeasons").GetInt32(), cur.GetProperty("establishedWeight").GetDouble()));
        Assert.Equal(cur.GetProperty("components").EnumerateArray().Sum(c => c.GetProperty("score").GetDouble() * c.GetProperty("weight").GetDouble()), cur.GetProperty("weightedSum").GetDouble(), 9);   // the document shows 5,033.3 from rounded scores
        Assert.Matches("^[0-9A-F]{64}$", cur.GetProperty("inputSha256").GetString()!);
        Assert.Equal("C", cur.GetProperty("band").GetProperty("band").GetString());
        Assert.Equal(41.0, cur.GetProperty("band").GetProperty("percentile").GetDouble(), 9);

        var comps = cur.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        Assert.Equal(("ndmi_min", 24.0, 67.0, 16.75), (comps["water_security"].GetProperty("feature").GetString(), comps["water_security"].GetProperty("featureValue").GetDouble(),
            comps["water_security"].GetProperty("score").GetDouble(), Math.Round(comps["water_security"].GetProperty("contribution").GetDouble(), 2)));
        Assert.Equal("yield_history", comps["yield_stability"].GetProperty("feature").GetString());
        Assert.Equal(JsonValueKind.Null, comps["yield_stability"].GetProperty("featureValue").ValueKind);
        Assert.Equal(1, b.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task AMissingComponentHasNoContributionAndTheEstablishedWeightIsRenormalised()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "P", 10);
        await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/cri", set.Admin, new { ndmiMin = 24.0, validFraction = 0.9 }));

        var cur = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/summary", set.Admin)))).GetProperty("current");

        Assert.Equal(25.0, cur.GetProperty("establishedWeight").GetDouble());
        Assert.Equal(67.0, cur.GetProperty("value").GetDouble(), 9);
        var soil = cur.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "soil_and_land");
        Assert.Equal((false, JsonValueKind.Null, JsonValueKind.Null), (soil.GetProperty("established").GetBoolean(), soil.GetProperty("score").ValueKind, soil.GetProperty("contribution").ValueKind));
        Assert.Equal(15.0, soil.GetProperty("weight").GetDouble());
    }

    [Fact]
    public async Task TheSummaryRoutesCheckPermissionsTenantAndExistence()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "P", 10);
        var parcelUrl = $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/summary";
        var farmUrl = FarmUrl(set, "summary");

        foreach (var url in new[] { parcelUrl, farmUrl })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, set.Outsider))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/nope/summary", set.Admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{set.OrgId}/risk/farms/nope/seasons/{Season}/summary", set.Admin))).StatusCode);
        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{set.OrgId}/risk/farms/{set.Farm.Id}/seasons/x/summary", set.Admin));
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.True(bad.StatusCode == HttpStatusCode.BadRequest, $"{(int)bad.StatusCode} {badBody}");
        Assert.Contains("invalid_season", badBody);
    }
}
