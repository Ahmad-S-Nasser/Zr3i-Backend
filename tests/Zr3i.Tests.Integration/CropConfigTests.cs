using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Crops;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Methods;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class CropConfigTests(MongoFixture mongo)
{
    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        await k.Methods.EnsureBuiltInsAsync();
        return k;
    }

    private static async Task<string[]> Refused(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    private static CropSettings Wheat() => new()
    {
        EmergenceToleranceDays = 7, PassMinSeparationDays = 10, SowingWindow = "11-01..12-15", CycleDays = 150,
        NdviThresholds = new() { ["greenup_pass_above"] = 0.45 },
    };

    [Fact]
    public async Task EachChangeIsANewSealedVersionAndOlderVersionsNeverChange()
    {
        var k = await NewAsync();

        var v1 = await k.Crops.CreateVersionAsync("admin-1", "wheat", Wheat(), "first values");
        var changed = Wheat();
        changed.EmergenceToleranceDays = 9;
        var v2 = await k.Crops.CreateVersionAsync("admin-1", "wheat", changed, "tolerance widened");

        Assert.Equal((1, 2), (v1.Version, v2.Version));
        Assert.Matches("^[0-9A-F]{64}$", v1.Sha256);
        Assert.NotEqual(v1.Sha256, v2.Sha256);
        var stored = await k.Crops.VersionsAsync("wheat");
        Assert.Equal([7, 9], stored.Select(v => v.Settings.EmergenceToleranceDays!.Value));
        Assert.All(stored, v => Assert.Equal(v.Sha256, v.ComputeSeal()));
    }

    [Fact]
    public async Task TheSealChangesWithEveryValueAndDoesNotDependOnInsertionOrder()
    {
        var baseline = new CropConfigVersion { CropCode = "wheat", Version = 1, Settings = Wheat() };
        var seal = baseline.ComputeSeal();

        foreach (var change in new Action<CropSettings>[]
        {
            s => s.EmergenceToleranceDays = 8, s => s.PassMinSeparationDays = 11, s => s.SowingWindow = "11-02..12-15", s => s.CycleDays = 151,
            s => s.NdviThresholds["greenup_pass_above"] = 0.46, s => s.NdviThresholds["hold_above"] = 0.6,
        })
        {
            var other = new CropConfigVersion { CropCode = "wheat", Version = 1, Settings = Wheat() };
            change(other.Settings);
            Assert.NotEqual(seal, other.ComputeSeal());
        }

        Assert.NotEqual(seal, new CropConfigVersion { CropCode = "wheat", Version = 2, Settings = Wheat() }.ComputeSeal());
        Assert.NotEqual(seal, new CropConfigVersion { CropCode = "maize", Version = 1, Settings = Wheat() }.ComputeSeal());

        var a = new CropSettings { NdviThresholds = new() { ["hold_above"] = 0.6, ["greenup_pass_above"] = 0.45 } };
        var b = new CropSettings { NdviThresholds = new() { ["greenup_pass_above"] = 0.45, ["hold_above"] = 0.6 } };
        Assert.Equal(new CropConfigVersion { CropCode = "x1", Version = 1, Settings = a }.ComputeSeal(), new CropConfigVersion { CropCode = "x1", Version = 1, Settings = b }.ComputeSeal());
    }

    [Fact]
    public async Task OnlyTheLatestActivationIsInForceAndARollbackIsAnotherActivation()
    {
        var k = await NewAsync();
        await k.Crops.CreateVersionAsync("a", "wheat", Wheat(), "v1");
        var second = Wheat();
        second.CycleDays = 160;
        await k.Crops.CreateVersionAsync("a", "wheat", second, "v2");
        Assert.Null(await k.Crops.ActiveAsync("wheat"));

        await k.Crops.ActivateAsync("a", "wheat", 1, "Dr. Partner");
        Assert.Equal(1, (await k.Crops.ActiveAsync("wheat"))!.Version);
        await k.Crops.ActivateAsync("a", "wheat", 2, "Dr. Partner");
        Assert.Equal(2, (await k.Crops.ActiveAsync("wheat"))!.Version);
        await k.Crops.ActivateAsync("a", "wheat", 1, "Dr. Partner");                      // rollback

        Assert.Equal(1, (await k.Crops.ActiveAsync("wheat"))!.Version);
        Assert.Equal(3, (await k.Store.CropConfigActivations.FindAsync(PlatformTenant.Scope, _ => true)).Count);   // nothing was overwritten
        Assert.Equal([("wheat", (int?)1)], await k.Crops.CropsAsync());
    }

    [Fact]
    public async Task ActivatingNeedsANamedApproverAndAnExistingVersionAndBothCreationAndActivationAreAudited()
    {
        var k = await NewAsync();
        await k.Crops.CreateVersionAsync("admin-1", "wheat", Wheat(), "first values");

        Assert.Contains("approver_required", await Refused(() => k.Crops.ActivateAsync("admin-1", "wheat", 1, "  ")));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Crops.ActivateAsync("admin-1", "wheat", 5, "Dr. Partner"));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Crops.ActivateAsync("admin-1", "maize", 1, "Dr. Partner"));
        Assert.Null(await k.Crops.ActiveAsync("wheat"));

        await k.Crops.ActivateAsync("admin-1", "wheat", 1, "Dr. Partner");

        var events = (await k.Store.Audit.FindAsync(PlatformTenant.Scope, e => e.Action.StartsWith("cropconfig."))).OrderBy(e => e.At).ToList();
        Assert.Equal(["cropconfig.created", "cropconfig.activated"], events.Select(e => e.Action));
        Assert.Contains("approved by Dr. Partner", events[1].Purpose);
        Assert.All(events, e => Assert.Equal("admin-1", e.ActorUserId));
    }

    [Theory]
    [InlineData("Wheat", "invalid_crop")]
    [InlineData("w", "invalid_crop")]
    [InlineData("", "invalid_crop")]
    public async Task ACropCodeMustBeALowercaseWord(string code, string expected)
    {
        var k = await NewAsync();
        Assert.Contains(expected, await Refused(() => k.Crops.CreateVersionAsync("a", code, Wheat(), "r")));
    }

    [Fact]
    public async Task ValuesAreCheckedWithStableCodesAndNothingIsStoredWhenAnyIsWrong()
    {
        var k = await NewAsync();
        var bad = new CropSettings
        {
            EmergenceToleranceDays = -1, PassMinSeparationDays = 0, CycleDays = 10, SowingWindow = "13-01..12-15",
            NdviThresholds = new() { ["hold_above"] = 1.5, ["colour_of_the_sky"] = 0.2 },
        };

        var codes = await Refused(() => k.Crops.CreateVersionAsync("a", "wheat", bad, " "));

        Assert.Equal(
            new[] { "invalid:cycle_days", "invalid:emergence_tolerance_days", "invalid:pass_min_separation_days", "invalid:sowing_window", "reason_required",
                    "threshold_out_of_range:hold_above", "unknown_threshold:colour_of_the_sky" }.Order(),
            codes.Order());
        Assert.Contains("empty_settings", await Refused(() => k.Crops.CreateVersionAsync("a", "wheat", new CropSettings(), "r")));
        Assert.Empty(await k.Crops.VersionsAsync("wheat"));
    }

    [Fact]
    public async Task ACropWithNothingActiveHasNoRuleExceptBeetWhoseMethodStartingRulesAreUsedAndLabelled()
    {
        var k = await NewAsync();
        var method = (await k.Methods.GetAsync("ZR3I-R0-2026-01"))!;

        Assert.Null(await k.Crops.ResolveAsync("wheat", method));
        await k.Crops.CreateVersionAsync("a", "wheat", Wheat(), "draft only");
        Assert.Null(await k.Crops.ResolveAsync("wheat", method));                         // created but not activated: not in force

        var beet = (await k.Crops.ResolveAsync("beet", method))!;
        Assert.Equal((CropRuleSources.MethodStartingRule, null), (beet.Source, beet.ConfigId));
        Assert.Equal(195, beet.Settings.CycleDays);
        Assert.Equal("08-15..11-15", beet.Settings.SowingWindow);
        Assert.Equal(0.25, beet.Settings.NdviThresholds["greenup_start_below"]);
        Assert.Equal(0.5, beet.Settings.NdviThresholds["greenup_pass_above"]);
        Assert.Equal(0.6, beet.Settings.NdviThresholds["hold_above"]);
        Assert.Equal(0.25, beet.Settings.NdviThresholds["sawtooth_drop"]);
        Assert.Null(beet.Settings.EmergenceToleranceDays);                                // not configured: never a guessed default

        await k.Crops.CreateVersionAsync("a", "wheat", Wheat(), "go");
        await k.Crops.ActivateAsync("a", "wheat", 2, "Dr. Partner");
        var wheat = (await k.Crops.ResolveAsync("wheat", method))!;
        Assert.Equal((CropRuleSources.CropConfig, 2), (wheat.Source, wheat.ConfigVersion));
        Assert.Equal((await k.Crops.ActiveAsync("wheat"))!.Sha256, wheat.Sha256);

        // An activated beet configuration replaces the method's starting rules.
        await k.Crops.CreateVersionAsync("a", "beet", new CropSettings { EmergenceToleranceDays = 6 }, "beet tolerance");
        await k.Crops.ActivateAsync("a", "beet", 1, "Dr. Partner");
        Assert.Equal(CropRuleSources.CropConfig, (await k.Crops.ResolveAsync("beet", method))!.Source);
    }

    [Fact]
    public void TheCropCollectionsAreAppendOnly()
    {
        var k = new KernelHarness(mongo);
        Assert.IsType<AppendOnlyRepository<CropConfigVersion>>(k.Store.CropConfigVersions);
        Assert.IsType<AppendOnlyRepository<CropConfigActivation>>(k.Store.CropConfigActivations);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task OnlyZr3iAdministratorsWriteEveryoneSignedInReadsAndActivationNeedsAnApprover()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await web.SignInAsync("founder@zr3i.example", await web.AcceptAsync((await web.Service<PlatformService>().BootstrapAsync("founder@zr3i.example"))!));
        var (_, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var ordinary = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var body = new { emergenceToleranceDays = 7, passMinSeparationDays = 10, sowingWindow = "11-01..12-15", cycleDays = 150, ndviThresholds = new Dictionary<string, double> { ["hold_above"] = 0.55 }, reason = "first" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync("/api/platform/crops/wheat/versions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/platform/crops/wheat/versions", ordinary, body))).StatusCode);

        var created = await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/platform/crops/wheat/versions", admin, body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Matches("^[0-9A-F]{64}$", (await Json(created)).GetProperty("sha256").GetString()!);

        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/platform/crops/wheat/versions", admin, new { emergenceToleranceDays = -3, reason = "x" }));
        Assert.Contains("invalid:emergence_tolerance_days", (await Json(bad)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));

        var act = "/api/platform/crops/wheat/versions/1/activate";
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, act, ordinary, new { approvedBy = "Dr. Partner" }))).StatusCode);
        var noApprover = await web.Http.SendAsync(web.Request(HttpMethod.Post, act, admin, new { approvedBy = "" }));
        Assert.Contains("approver_required", (await Json(noApprover)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Request(HttpMethod.Post, act, admin, new { approvedBy = "Dr. Partner" }))).StatusCode);

        var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/crops", ordinary)));
        Assert.Equal(("wheat", 1), (list[0].GetProperty("code").GetString(), list[0].GetProperty("activeVersion").GetInt32()));
        var versions = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/crops/wheat/versions", ordinary)));
        Assert.True(versions[0].GetProperty("active").GetBoolean());
        Assert.Equal(0.55, versions[0].GetProperty("settings").GetProperty("ndviThresholds").GetProperty("hold_above").GetDouble());
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/crops/nothing/versions", ordinary))).StatusCode);
    }
}
