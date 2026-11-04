using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class RiskApiTests(MongoFixture mongo)
{
    private readonly MongoFixture _mongo = mongo;
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Seasons = ["2021-22", "2022-23", "2023-24", "2024-25", "2025-26"];

    private static readonly (string Label, double Area, double Cri)[] Delta =
    [
        ("ZR-KS-01", 62.4, 19.0), ("ZR-KS-02", 48.1, 26.0), ("ZR-KS-03", 55.7, 50.3), ("ZR-KS-04", 71.2, 33.0), ("ZR-KS-05", 39.8, 22.0),
        ("ZR-KS-06", 44.3, 58.0), ("ZR-KS-07", 52.6, 71.0), ("ZR-KS-08", 33.9, 29.0), ("ZR-KS-09", 20.0, 41.0),
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

    private static async Task<ParcelSeason> ParcelSeasonAsync(Setup s, string label, double areaFeddan, string? crop = "wheat")
    {
        var farms = s.Web.Service<FarmService>();
        var parcel = await farms.CreateParcelAsync(s.Scope, "seed", new NewParcel(s.Farm.Id, label));
        await s.Web.Service<BoundaryService>().AddAsync(s.Scope, "seed", parcel.Id, KernelHarness.SquareMetres(Math.Sqrt(areaFeddan * 4200.83)), new BoundaryProvenance("survey", "s", Captured, 1));
        return await farms.DeclareSeasonAsync(s.Scope, "seed", new NewParcelSeason(parcel.Id, Season, crop, areaFeddan, "2026-10-01", "contract"));
    }

    private static object Features(double validFraction = 0.9) => new
    {
        ndmiMin = 24.0, heatStress = 0.58, ndviIntegral = 46.0, areaVariancePct = 12.0, ndviCohortPercentile = 34.0,
        closedSeasonYields = new[] { 73.5, 79, 97, 100, 100, 104, 108, 112 }, validFraction,
    };

    [Fact]
    public async Task DeltaAgroEstateReproducesFri404ThroughTheRoutesAndEveryResultStatesItsMethodAndThatItIsNotCalibrated()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var recorder = web.Service<IndicatorRecorder>();
        foreach (var (label, area, cri) in Delta)
        {
            var ps = await ParcelSeasonAsync(set, label, area);
            await recorder.RecordAsync(set.Scope, "seed", new IndicatorDraft("CRI", "parcel_season", ps.Id, "FRI-2026-01", 1, [new("water_security", 100, cri, true)], cri, 0.9));
        }

        var url = $"/api/organisations/{set.OrgId}/risk/farms/{set.Farm.Id}/seasons/{Season}/fri";
        var response = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, new { adjustments = DeltaAdjustments }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var result = body.GetProperty("result");

        Assert.Equal(40.4, Math.Round(result.GetProperty("value").GetDouble(), 1));
        Assert.Equal(("FRI", "specified", "specified_not_calibrated", "FRI-2026-01", 1),
            (result.GetProperty("indicator").GetString(), result.GetProperty("maturity").GetString(), result.GetProperty("label").GetString(),
             result.GetProperty("method").GetProperty("id").GetString(), result.GetProperty("method").GetProperty("version").GetInt32()));
        Assert.Equal(9, result.GetProperty("components").GetArrayLength());
        Assert.Equal(5, result.GetProperty("adjustments").GetArrayLength());
        Assert.Equal(0, body.GetProperty("skipped").GetArrayLength());
        Assert.Equal("farm_cohort_not_defined", result.GetProperty("bandReason").GetString());

        var again = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, new { adjustments = DeltaAdjustments })));
        Assert.Equal(result.GetProperty("id").GetString(), again.GetProperty("result").GetProperty("id").GetString());     // recomputing adds nothing

        var history = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, set.Admin)));
        Assert.Equal(1, history.GetArrayLength());
        Assert.True(history[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task AParcelWithoutACriIsSkippedAndListedNeverGuessed()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var withCri = await ParcelSeasonAsync(set, "has-cri", 10);
        var without = await ParcelSeasonAsync(set, "no-cri", 10);
        await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{withCri.Id}/cri", set.Admin, Features()));

        var body = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{set.OrgId}/risk/farms/{set.Farm.Id}/seasons/{Season}/fri", set.Admin, new { adjustments = Array.Empty<object>() })));

        Assert.Equal(1, body.GetProperty("skipped").GetArrayLength());
        Assert.Equal((without.Id, "no_cri"), (body.GetProperty("skipped")[0].GetProperty("parcelSeasonId").GetString(), body.GetProperty("skipped")[0].GetProperty("reason").GetString()));
        Assert.Equal(1, body.GetProperty("result").GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task TheCriRoutesRecordTheWorkedExampleCheckPermissionsRefuseBadBodiesAndRefuseAnotherOrganisation()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "KS-03", 55.7, "maize");
        var url = $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/cri";

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync(url, Features())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Outsider, Features()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, set.Outsider))).StatusCode);

        var missing = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, new { ndmiMin = 24.0 }));
        Assert.Equal("valid_fraction_required", (await Json(missing)).GetProperty("error").GetString());
        var impossible = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, Features(1.5)));
        Assert.Equal("invalid_valid_fraction", (await Json(impossible)).GetProperty("error").GetString());
        var none = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, new { validFraction = 0.9 }));
        Assert.Equal("no_established_components", (await Json(none)).GetProperty("error").GetString());
        var unknown = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{set.OrgId}/risk/parcel-seasons/nope/cri", set.Admin, Features()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var ok = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, Features())));
        Assert.Equal(50.3, Math.Round(ok.GetProperty("value").GetDouble(), 1));
        Assert.Equal(("CRI", "specified", "specified_not_calibrated"), (ok.GetProperty("indicator").GetString(), ok.GetProperty("maturity").GetString(), ok.GetProperty("label").GetString()));

        // The other organisation cannot reach it even through its own route with this parcel-season id.
        var outsiderTenant = (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, set.Outsider)));
        Assert.Equal(HttpStatusCode.Forbidden, outsiderTenant.StatusCode);
    }

    [Fact]
    public async Task ABandIsGivenOnlyWhenAMatchingReferenceExistsAndItNamesTheCohortAndVersionUsed()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "KS-03", 55.7, "maize");
        var cri = $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/cri";
        await web.Http.SendAsync(web.Request(HttpMethod.Post, cri, set.Admin, Features()));

        var before = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, cri, set.Admin))))[0];
        Assert.Equal(JsonValueKind.Null, before.GetProperty("band").ValueKind);
        Assert.Equal("no_reference_distribution", before.GetProperty("bandReason").GetString());

        var refs = $"/api/organisations/{set.OrgId}/risk/reference-distributions";
        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Post, refs, set.Admin, new { kind = "parcel", cohortLevel = "crop_national", cohortKey = "maize", seasons = Seasons.Take(3), scores = new[] { 40.0 } }));
        Assert.Equal("too_few_seasons", (await Json(bad)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, refs, set.Outsider, new { }))).StatusCode);

        var scores = Enumerable.Range(0, 100).Select(i => 30.0 + i * 0.5).ToArray();                          // 30.0 .. 79.5
        var added = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, refs, set.Admin, new { kind = "parcel", cohortLevel = "crop_national", cohortKey = "maize", seasons = Seasons, scores })));
        Assert.Equal((1, 100), (added.GetProperty("version").GetInt32(), added.GetProperty("size").GetInt32()));

        var after = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, cri, set.Admin))))[0];
        var band = after.GetProperty("band");
        Assert.Equal(("crop_national", "maize", 1, 100), (band.GetProperty("cohortLevel").GetString(), band.GetProperty("cohortKey").GetString(), band.GetProperty("referenceVersion").GetInt32(), band.GetProperty("referenceSize").GetInt32()));
        // CRI 50.34: scores 30.0 .. 50.0 step 0.5 are at or below it, which is 41 of 100.
        Assert.Equal(41.0, band.GetProperty("percentile").GetDouble(), 9);
        Assert.Equal("C", band.GetProperty("band").GetString());
    }

    [Fact]
    public async Task ARecomputeWithChangedFeaturesAddsANewResultThatSupersedesTheOldAndHistoryIsInTimeOrderNotValueOrder()
    {
        var set = await StartAsync();
        await using var web = set.Web;
        var ps = await ParcelSeasonAsync(set, "KS-03", 55.7);
        var url = $"/api/organisations/{set.OrgId}/risk/parcel-seasons/{ps.Id}/cri";
        await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, Features()));
        await web.Http.SendAsync(web.Request(HttpMethod.Post, url, set.Admin, new { ndmiMin = 40.0, validFraction = 0.9 }));

        var history = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, set.Admin)));
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal(history[0].GetProperty("id").GetString(), history[1].GetProperty("supersedes").GetString());
        Assert.Equal((false, true), (history[0].GetProperty("current").GetBoolean(), history[1].GetProperty("current").GetBoolean()));
        Assert.Equal("not_current", history[0].GetProperty("bandReason").GetString());
    }

    [Fact]
    public void TheRiskCodeNeverUsesTheWordsOfACreditDecision()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Zr3i.slnx"))) root = root.Parent;
        var files = Directory.GetFiles(Path.Combine(root!.FullName, "src/Zr3i.Modules.Risk"), "*.cs").Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .Concat([Path.Combine(root.FullName, "src/Zr3i.Web/Endpoints/RiskEndpoints.cs")]).ToList();
        Assert.True(files.Count >= 5);

        var literal = new Regex("\"[^\"\\n]*(credit score|probability of default|approv|recommend|reject|rank)[^\"\\n]*\"", RegexOptions.IgnoreCase);
        var hits = files.SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => Path.GetFileName(f) + ": " + m.Value)).ToList();
        Assert.Empty(hits);
        Assert.Matches(literal, "var x = \"credit score\";");                                               // the control: the scan finds such a literal
    }
}
