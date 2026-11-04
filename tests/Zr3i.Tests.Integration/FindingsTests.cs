using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel;
using Zr3i.Kernel.Crops;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Zr3i.Modules.Verification;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class FindingsTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("find-a");
    private static readonly TenantScope B = TenantScope.ForTenant("find-b");
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sowing = new(2026, 10, 1, 8, 51, 0, DateTimeKind.Utc);
    private const string Season = "2026-27";

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        await k.Methods.EnsureBuiltInsAsync();
        return k;
    }

    private static async Task<(Parcel Parcel, ParcelSeason Season, BoundaryVersion? Boundary)> ParcelAsync(
        KernelHarness k, TenantScope scope, string label, double? side, string? crop = "beet", double? declared = 5.0, string? sowing = "2026-10-01")
    {
        var farm = await k.Farms.CreateFarmAsync(scope, "u", new NewFarm("F " + label));
        var parcel = await k.Farms.CreateParcelAsync(scope, "u", new NewParcel(farm.Id, label));
        BoundaryVersion? boundary = null;
        if (side is not null) boundary = await k.Boundaries.AddAsync(scope, "u", parcel.Id, KernelHarness.SquareMetres(side.Value), new BoundaryProvenance("survey", "s", Captured, 1));
        var season = await k.Farms.DeclareSeasonAsync(scope, "u", new NewParcelSeason(parcel.Id, Season, crop, declared, sowing, "contract"));
        return (parcel, season, boundary);
    }

    private static async Task ObserveAsync(KernelHarness k, TenantScope scope, Parcel parcel, BoundaryVersion boundary, params (int Day, double Ndvi)[] points)
    {
        foreach (var (day, ndvi) in points)
            await k.Store.ParcelObservations.InsertAsync(scope, new ParcelObservation
            {
                ParcelId = parcel.Id, BoundaryId = boundary.Id, SceneId = $"S2-{scope.TenantId}-{day:000}", SensingTime = Sowing.AddDays(day), Readable = true,
                FullPixels = 200, UsablePixels = 200, NdviMedian = ndvi, PixelStackSha256 = new string('A', 64), MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, MethodState = "draft",
            });
    }

    private static readonly (int, double)[] BeetCurve = [(10, 0.12), (50, 0.4), (80, 0.65), (120, 0.8), (160, 0.78)];

    private static async Task<string> AssessAsync(KernelHarness k, TenantScope scope, SizeClassScheme? sizes = null)
    {
        var job = await k.Data.Jobs.EnqueueAsync(scope, AssessmentPlanner.JobType, JobJson.Serialize(new AssessPayload(Season)), "u", k.Clock.GetUtcNow().UtcDateTime);
        var runner = new JobRunner(k.Data.CreateJobClaimer(new JobQueueOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.Zero)),
            [new AssessHandler(k.Store, k.Methods, k.Crops, sizes, k.Clock)], k.Clock, "assess-worker");
        await runner.RunUntilIdleAsync();
        var done = (await k.Data.Jobs.GetAsync(scope, job.Id))!;
        Assert.True(done.Status == "succeeded", done.Status + ":" + done.ErrorCode);
        return done.ResultJson!;
    }

    private static async Task<List<Finding>> FindingsOf(KernelHarness k, TenantScope scope, Parcel parcel) =>
        [.. (await k.Store.Findings.FindAsync(scope, f => f.ParcelId == parcel.Id)).OrderBy(f => f.CreatedAt).ThenBy(f => f.Slot, StringComparer.Ordinal)];

    [Fact]
    public async Task AnAreaGapBeyondTheFieldsOwnNoiseIsFlaggedWithTheSignedGapAndWhereItCameFrom()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "over", 150, declared: 10.0);        // 150 m square = 5.36 feddan measured, 10 declared

        await AssessAsync(k, A);

        var area = (await FindingsOf(k, A, parcel)).Single(f => f.Slot == FindingSlots.Area);
        Assert.Equal((FindingTypes.AreaGap, FindingStatuses.Flagged, null), (area.Type, area.Status, area.Reason));
        Assert.Equal("OverDeclared", area.Direction);
        Assert.Equal(boundary!.AreaFeddan, area.MeasuredFeddan!.Value, 9);
        Assert.Equal(area.MeasuredFeddan!.Value - 10.0, area.GapFeddan!.Value, 9);
        Assert.True(area.GapFeddan < 0);                                                         // signed: measured minus declared
        Assert.Equal(100 * 4 * 150 * 2 / (150.0 * 150), area.NoisePercent!.Value, 4);
        Assert.Equal((boundary.Id, boundary.GeometrySha256), (area.BoundaryId, area.BoundaryGeometrySha256));
        Assert.Equal(("ZR3I-R0-2026-01", 1, "draft"), (area.MethodId, area.MethodVersion, area.MethodState));
        Assert.Equal((10.0, "beet", "2026-10-01", "contract"), (area.DeclaredAreaFeddan, area.DeclaredCrop, area.DeclaredSowingDate, area.DeclaredSource));
        Assert.Equal("ZR3I-R0-2026-01/v1/area_gap/-/unclassified", area.AccuracyRef);
        Assert.Matches("^[0-9A-F]{64}$", area.InputSha256);
        Assert.Null(area.Supersedes);
    }

    [Fact]
    public async Task AnAreaGapInsideTheNoiseIsOnlyMeasuredNeverFlagged()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "close", 150, declared: Math.Round(150.0 * 150 / 4200.83 * 1.02, 4));   // declared 2 percent above; noise is 5.3 percent

        await AssessAsync(k, A);

        var area = (await FindingsOf(k, A, parcel)).Single(f => f.Slot == FindingSlots.Area);
        Assert.Equal((FindingStatuses.Measured, "WithinNoise"), (area.Status, area.Direction));
        Assert.True(area.GapPercent < area.NoisePercent);
    }

    [Fact]
    public async Task ACurveThatFitsTheDeclaredCropIsStatedAsCropPresentWithItsEvidenceAndTheRuleSourceNamed()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "beet", 150);
        await ObserveAsync(k, A, parcel, boundary!, BeetCurve);

        await AssessAsync(k, A);

        var crop = (await FindingsOf(k, A, parcel)).Single(f => f.Slot == FindingSlots.Crop);
        Assert.Equal((FindingTypes.CropPresent, FindingStatuses.Measured, null), (crop.Type, crop.Status, crop.Reason));
        Assert.Equal((5, 0.8), (crop.ScenesUsed, crop.MaxNdvi));
        Assert.Equal(["greenup", "hold", "no_sawtooth"], crop.RulesTested);
        Assert.Equal(5, crop.ObservationIds.Count);
        Assert.Equal((CropRuleSources.MethodStartingRule, null), (crop.CropRuleSource, crop.CropConfigId));
        Assert.Matches("^[0-9A-F]{64}$", crop.CropRuleSha256!);
        Assert.Equal("ZR3I-R0-2026-01/v1/crop_present/beet/unclassified", crop.AccuracyRef);
    }

    [Fact]
    public async Task ASizeClassSchemeFillsTheStratumInTheAccuracyReference()
    {
        var k = await NewAsync();
        var (parcel, _, _) = await ParcelAsync(k, A, "sized", 150);                              // 5.36 feddan

        await AssessAsync(k, A, new SizeClassScheme([1, 5, 10]));

        Assert.All(await FindingsOf(k, A, parcel), f => Assert.EndsWith("/5-10", f.AccuracyRef, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningAgainWritesNothingNewAndAChangeAddsOneFindingThatSupersedesTheOldWithoutTouchingTheOtherQuestion()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "again", 150, declared: 10.0);
        await ObserveAsync(k, A, parcel, boundary!, BeetCurve);

        var first = await AssessAsync(k, A);
        var second = await AssessAsync(k, A);

        Assert.Contains("\"written\":2", first, StringComparison.Ordinal);
        Assert.Contains("\"written\":0", second, StringComparison.Ordinal);
        Assert.Equal(2, (await FindingsOf(k, A, parcel)).Count);

        // A new readable scene changes the crop evidence only.
        await ObserveAsync(k, A, parcel, boundary!, (190, 0.3));
        await AssessAsync(k, A);

        var all = await FindingsOf(k, A, parcel);
        Assert.Equal(3, all.Count);
        var cropRows = all.Where(f => f.Slot == FindingSlots.Crop).OrderBy(f => f.CreatedAt).ToList();
        Assert.Equal(2, cropRows.Count);
        Assert.Equal(cropRows[0].Id, cropRows[1].Supersedes);
        Assert.Equal(6, cropRows[1].ObservationIds.Count);
        Assert.Single(all, f => f.Slot == FindingSlots.Area);                                    // the area finding did not change
    }

    [Fact]
    public async Task ActivatingACropConfigurationMakesANewFindingThatCitesIt()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "cfg", 150);
        await ObserveAsync(k, A, parcel, boundary!, BeetCurve);
        await AssessAsync(k, A);

        var thresholds = new SortedDictionary<string, double> { ["greenup_start_below"] = 0.25, ["greenup_pass_above"] = 0.5, ["hold_above"] = 0.6, ["sawtooth_drop"] = 0.25 };
        var v = await k.Crops.CreateVersionAsync("admin", "beet", new CropSettings { CycleDays = 195, PassMinSeparationDays = 20, NdviThresholds = thresholds }, "first beet config");
        await k.Crops.ActivateAsync("admin", "beet", 1, "Dr. Partner");
        await AssessAsync(k, A);

        var crop = (await FindingsOf(k, A, parcel)).Where(f => f.Slot == FindingSlots.Crop).OrderBy(f => f.CreatedAt).ToList();
        Assert.Equal(2, crop.Count);
        Assert.Equal((CropRuleSources.CropConfig, v.Id, 1, v.Sha256), (crop[1].CropRuleSource, crop[1].CropConfigId, crop[1].CropConfigVersion, crop[1].CropRuleSha256));
        Assert.Equal(crop[0].Id, crop[1].Supersedes);
    }

    [Fact]
    public async Task ATinyParcelIsNeverNoCropHoweverBareItLooksButItsAreaGapStillComesFromTheBoundary()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "tiny", 30, declared: 1.0);
        await ObserveAsync(k, A, parcel, boundary!, (10, 0.1), (70, 0.1), (100, 0.1), (130, 0.1));

        await AssessAsync(k, A);

        var rows = await FindingsOf(k, A, parcel);
        var crop = rows.Single(f => f.Slot == FindingSlots.Crop);
        Assert.Equal((FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.BelowResolution), (crop.Type, crop.Status, crop.Reason));
        Assert.DoesNotContain(rows, f => f.Type == FindingTypes.NoCrop);
        Assert.Equal(FindingTypes.AreaGap, rows.Single(f => f.Slot == FindingSlots.Area).Type);
        Assert.NotEqual(FindingStatuses.NotAssessable, rows.Single(f => f.Slot == FindingSlots.Area).Status);
    }

    [Fact]
    public async Task BareScenesWithoutAConfiguredSeparationAreOnlyPendingAndAConfiguredOneFlagsThemOnTwoPasses()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "bare", 150);
        await ObserveAsync(k, A, parcel, boundary!, (10, 0.12), (70, 0.13), (100, 0.15), (130, 0.14));

        await AssessAsync(k, A);
        var pending = (await FindingsOf(k, A, parcel)).Single(f => f.Slot == FindingSlots.Crop);
        Assert.Equal((FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.PassSeparationNotConfigured), (pending.Type, pending.Status, pending.Reason));

        var thresholds = new SortedDictionary<string, double> { ["greenup_start_below"] = 0.25, ["greenup_pass_above"] = 0.5, ["hold_above"] = 0.6, ["sawtooth_drop"] = 0.25 };
        await k.Crops.CreateVersionAsync("admin", "beet", new CropSettings { CycleDays = 195, PassMinSeparationDays = 20, NdviThresholds = thresholds }, "separation");
        await k.Crops.ActivateAsync("admin", "beet", 1, "Dr. Partner");
        await AssessAsync(k, A);

        var flagged = (await FindingsOf(k, A, parcel)).Where(f => f.Slot == FindingSlots.Crop).OrderBy(f => f.CreatedAt).Last();
        Assert.Equal((FindingTypes.NoCrop, FindingStatuses.Flagged, null, 3), (flagged.Type, flagged.Status, flagged.Reason, flagged.PassesUsed));
        Assert.Equal(pending.Id, flagged.Supersedes);
    }

    [Fact]
    public async Task MissingPiecesGiveNotAssessableWithAReasonNeverAGuess()
    {
        var k = await NewAsync();
        var noBoundary = await ParcelAsync(k, A, "nob", null);
        var noArea = await ParcelAsync(k, A, "noarea", 150, declared: null);
        var noSowing = await ParcelAsync(k, A, "nosow", 150, sowing: null);
        var wheat = await ParcelAsync(k, A, "wheat", 150, crop: "wheat");
        var noCrop = await ParcelAsync(k, A, "nocrop", 150, crop: null);
        foreach (var p in new[] { noSowing, wheat, noCrop }) await ObserveAsync(k, A, p.Parcel, p.Boundary!, BeetCurve);

        await AssessAsync(k, A);

        async Task<(string?, string?)> Reasons(Parcel parcel)
        {
            var rows = await FindingsOf(k, A, parcel);
            return (rows.Single(f => f.Slot == FindingSlots.Area).Reason, rows.Single(f => f.Slot == FindingSlots.Crop).Reason);
        }

        Assert.Equal((FindingReasons.NoBoundary, FindingReasons.NoBoundary), await Reasons(noBoundary.Parcel));
        Assert.Equal((FindingReasons.MissingDeclaration, FindingReasons.InsufficientScenes), await Reasons(noArea.Parcel));   // no scenes were added for this parcel
        Assert.Equal((null, FindingReasons.MissingDeclaration), await Reasons(noSowing.Parcel));
        Assert.Equal((null, FindingReasons.NoRuleForCrop), await Reasons(wheat.Parcel));
        Assert.Equal((null, FindingReasons.MissingDeclaration), await Reasons(noCrop.Parcel));
    }

    [Fact]
    public async Task OnlyObservationsOfTheCurrentBoundaryCountAndUnreadableOnesNeverDo()
    {
        var k = await NewAsync();
        var (parcel, _, boundary) = await ParcelAsync(k, A, "obs", 150);
        await ObserveAsync(k, A, parcel, boundary!, BeetCurve);
        // An unreadable scene and a scene of an older boundary: neither is evidence for the current one.
        await k.Store.ParcelObservations.InsertAsync(A, new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = boundary!.Id, SceneId = "S2-unreadable", SensingTime = Sowing.AddDays(20), Readable = false, NdviMedian = null, PixelStackSha256 = "X",
        });
        await k.Store.ParcelObservations.InsertAsync(A, new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = "older-boundary", SceneId = "S2-older", SensingTime = Sowing.AddDays(30), Readable = true, NdviMedian = 0.9, PixelStackSha256 = "Y",
        });

        await AssessAsync(k, A);

        var crop = (await FindingsOf(k, A, parcel)).Single(f => f.Slot == FindingSlots.Crop);
        Assert.Equal(5, crop.ScenesUsed);
        Assert.Equal(FindingTypes.CropPresent, crop.Type);
    }

    [Fact]
    public async Task OneOrganisationsAssessmentNeverReadsOrWritesAnotherAndFindingsAreAppendOnly()
    {
        var k = await NewAsync();
        var a = await ParcelAsync(k, A, "iso-a", 150, declared: 10.0);
        var b = await ParcelAsync(k, B, "iso-b", 150, declared: 10.0);

        await AssessAsync(k, A);

        Assert.Equal(2, (await k.Store.Findings.FindAsync(A, _ => true)).Count);
        Assert.Empty(await k.Store.Findings.FindAsync(B, _ => true));
        Assert.Empty(await FindingsOf(k, A, b.Parcel));
        Assert.IsType<AppendOnlyRepository<Finding>>(k.Store.Findings);
    }

    [Fact]
    public async Task TheFindingCodeNeverUsesTheWordsApproveRejectOrRecommend()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Zr3i.slnx"))) root = root.Parent;
        var files = new[] { "src/Zr3i.Modules.Verification", "src/Zr3i.Kernel/Findings" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root!.FullName, d), "*.cs"))
            .Append(Path.Combine(root!.FullName, "src/Zr3i.Web/Endpoints/FindingEndpoints.cs")).ToList();
        Assert.NotEmpty(files);

        var literal = new Regex("\"[^\"\\n]*(approv|reject|recommend|verdict)[^\"\\n]*\"", RegexOptions.IgnoreCase);
        var hits = files.SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => Path.GetFileName(f) + ": " + m.Value)).ToList();
        Assert.Empty(hits);

        // The control: the scan does find such a literal.
        Assert.Matches(literal, "var x = \"recommend this\";");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task TheRoutesQueueWorkCheckPermissionsAndShowEachFindingWithItsHistoryAndSources()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var (_, otherInvite) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other");
        var outsider = await web.SignInAsync("admin@other.example", await web.AcceptAsync(otherInvite));
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F"));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "P"));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));
        var url = $"/api/organisations/{orgId}/verification/assess";

        var none = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, new { season = Season }));
        Assert.Equal("no_parcel_seasons", (await Json(none)).GetProperty("codes")[0].GetString());
        await web.Service<FarmService>().DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, Season, "beet", 10.0, "2026-10-01", "contract"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync(url, new { season = Season })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url, outsider, new { season = Season }))).StatusCode);
        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, new { season = "?" }));
        Assert.Contains("invalid_season", (await Json(bad)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.Accepted, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url, admin, new { season = Season }))).StatusCode);

        var runner = new JobRunner(web.Service<DataStore>().CreateJobClaimer(), [new AssessHandler(web.Service<KernelStore>(), web.Service<Zr3i.Kernel.Methods.MethodRegistry>(), web.Service<CropConfigs>(), null, TimeProvider.System)], TimeProvider.System, "w");
        Assert.Equal(1, await runner.RunUntilIdleAsync());

        var rows = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/parcels/{parcel.Id}/findings", admin)));
        var area = rows.EnumerateArray().Single(f => f.GetProperty("type").GetString() == "area_gap");
        Assert.Equal(("FLAGGED", true, "draft"), (area.GetProperty("status").GetString(), area.GetProperty("current").GetBoolean(), area.GetProperty("method").GetProperty("state").GetString()));
        Assert.Equal(10.0, area.GetProperty("declared").GetProperty("areaFeddan").GetDouble());
        Assert.True(area.GetProperty("area").GetProperty("gapFeddan").GetDouble() < 0);
        var crop = rows.EnumerateArray().Single(f => f.GetProperty("type").GetString() == "not_assessable");
        Assert.Equal("insufficient_scenes", crop.GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/parcels/{parcel.Id}/findings", outsider))).StatusCode);
    }
}
