using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Zr3i.Kernel.Programmes;
using Zr3i.Kernel.Reports;
using Zr3i.Reports;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Frozen reports: asking queues a job, the worker handler freezes the files, every file matches the manifest, and each organisation sees only its own.</summary>
[Collection(MongoCollection.Name)]
public class ReportTests
{
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakePdf(bool broken = false) : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, DateTime createdAtUtc, CancellationToken ct) =>
            broken ? throw new InvalidOperationException("browser_not_configured") : Task.FromResult(Encoding.UTF8.GetBytes("%PDF-fake " + html.Length));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));
    private static Task<HttpResponseMessage> Post(WebHarness web, string url, string token, object body) => web.Http.SendAsync(web.Request(HttpMethod.Post, url, token, body));

    private sealed record Party(string OrgId, string Token);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName, string type = "bank", string role = "org-admin")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, role, orgName, type);
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    /// <summary>A bank with one tranche of two parcels (one flagged, one confirmed).</summary>
    private static async Task<(Party Bank, Tranche Tranche, Farm Farm)> SeedBankAsync(WebHarness web, string email = "admin@nile-bank.example", string name = "Nile Bank")
    {
        var bank = await PartyAsync(web, email, name);
        var scope = TenantScope.ForTenant(bank.OrgId);
        var farms = web.Service<FarmService>();
        var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Farm A"));
        var ids = new List<string>();
        foreach (var (label, status) in new[] { ("P-1", "MEASURED"), ("P-2", "FLAGGED") })
        {
            var p = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label, ExternalId: label));
            await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(150), new BoundaryProvenance("survey", "s", Captured, 1));
            var ps = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(p.Id, Season, "wheat", 5.0, "2026-10-01", "contract"));
            await web.Service<KernelStore>().Findings.InsertAsync(scope, new Finding
            {
                ParcelId = p.Id, ParcelSeasonId = ps.Id, Season = Season, Slot = FindingSlots.Area, Type = "area_gap", Status = status, GapPercent = status == "FLAGGED" ? 30 : 1, NoisePercent = 5.3,
                InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow,
            });
            ids.Add(p.Id);
        }

        var tranche = await web.Service<TrancheService>().CreateAsync(scope, "seed", new TrancheRequest("TR-01", Season, "2026-12-01", new ParcelSelection(null, ids, null)));
        return (bank, tranche, farm);
    }

    private static ReportFreezeHandler Handler(WebHarness web, IPdfRenderer pdf) =>
        new(web.Service<KernelStore>(), new SnapshotBuilder(web.Service<KernelStore>(), web.Service<TrancheService>(), web.Service<Zr3i.Modules.Risk.RiskService>(), TimeProvider.System), pdf, web.Service<AuditService>(), TimeProvider.System, web.Service<Zr3i.Kernel.Notifications.NotificationService>());

    private static async Task<JobOutcome> RunNewestFreezeJobAsync(WebHarness web, string orgId, IPdfRenderer? pdf = null)
    {
        var scope = TenantScope.ForTenant(orgId);
        var data = web.Service<DataStore>();
        var job = (await data.Jobs.ListAsync(scope, ReportService.FreezeJobType)).Last();
        return await Handler(web, pdf ?? new FakePdf()).HandleAsync(new JobContext(job, data.CreateJobClaimer(), "test"), CancellationToken.None);
    }

    private static async Task<string> RequestAsync(WebHarness web, Party who, object body)
    {
        var response = await Post(web, $"/api/organisations/{who.OrgId}/reports", who.Token, body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("reportId").GetString()!;
    }

    [Fact]
    public async Task AskingForAReportQueuesAJobAndListsItAsPendingUntilTheWorkerFreezesIt()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);

        var id = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        var before = await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token));
        Assert.Empty(before.GetProperty("ready").EnumerateArray());
        var pending = Assert.Single(before.GetProperty("pending").EnumerateArray());
        Assert.Equal(id, pending.GetProperty("reportId").GetString());
        Assert.Equal("queued", pending.GetProperty("status").GetString());

        Assert.True((await RunNewestFreezeJobAsync(web, bank.OrgId)).Succeeded);
        var after = await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token));
        Assert.Equal(id, Assert.Single(after.GetProperty("ready").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task EveryFileMatchesItsHashAndTheManifestListsThemAll()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var id = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        Assert.True((await RunNewestFreezeJobAsync(web, bank.OrgId)).Succeeded);

        var report = await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}", bank.Token));
        var names = report.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["manifest.json", "report.ar.html", "report.ar.pdf", "report.csv", "report.en.html", "report.en.pdf", "snapshot.json"], names);

        var bytes = new Dictionary<string, byte[]>();
        foreach (var f in report.GetProperty("files").EnumerateArray())
        {
            var name = f.GetProperty("name").GetString()!;
            var download = await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}/files/{name}", bank.Token);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            bytes[name] = await download.Content.ReadAsByteArrayAsync();
            var sha = Convert.ToHexString(SHA256.HashData(bytes[name])).ToLowerInvariant();
            Assert.Equal(f.GetProperty("sha256").GetString(), sha);
            Assert.Equal(sha, download.Headers.GetValues("X-Content-SHA256").Single());
        }

        var manifest = JsonDocument.Parse(bytes["manifest.json"]).RootElement;
        Assert.Equal(report.GetProperty("manifestSha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes["manifest.json"])).ToLowerInvariant());
        var listed = manifest.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("sha256").GetString()!);
        Assert.Equal(names.Where(n => n != "manifest.json").Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, sha) in listed) Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes[name])).ToLowerInvariant(), sha);
        Assert.Equal(report.GetProperty("snapshotSha256").GetString(), manifest.GetProperty("snapshotSha256").GetString());
        Assert.Equal(report.GetProperty("snapshotSha256").GetString(), ReportSnapshot.FromJson(Encoding.UTF8.GetString(bytes["snapshot.json"])).Sha256());
    }

    [Fact]
    public async Task TheTrancheReportStatesTheCountsAndTheFlaggedParcelInBothLanguages()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var id = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);

        async Task<string> Page(string name) => await (await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}/files/{name}", bank.Token)).Content.ReadAsStringAsync();
        var en = await Page("report.en.html");
        var ar = await Page("report.ar.html");
        Assert.Contains("Tranche report", en);
        Assert.Contains("TR-01", en);
        Assert.Contains("P-2", en);
        Assert.DoesNotContain("<td class=\"n\"><bdi dir=\"ltr\">P-1</bdi></td>", en);
        Assert.Contains("dir=\"rtl\"", ar);
        Assert.Contains("تقرير الشريحة", ar);
        var csv = await Page("report.csv");
        Assert.StartsWith("parcel,farm,verdict,measured,declared,gap", csv);
        Assert.Contains("P-2,Farm A,FLAGGED", csv);
    }

    [Fact]
    public async Task AFarmRiskReportCarriesTheMethodAndTheMaturityLabel()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, _, farm) = await SeedBankAsync(web);
        var id = await RequestAsync(web, bank, new { kind = "farm_risk", subjectId = farm.Id, season = Season });
        Assert.True((await RunNewestFreezeJobAsync(web, bank.OrgId)).Succeeded);

        var report = await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}", bank.Token));
        Assert.StartsWith("FRI-2026-01", report.GetProperty("methodVersions").GetString());
        var html = await (await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}/files/report.en.html", bank.Token)).Content.ReadAsStringAsync();
        Assert.Contains("Specified · not calibrated", html);
        Assert.Contains(report.GetProperty("snapshotSha256").GetString()!, html);
    }

    [Fact]
    public async Task RunningTheSameJobTwiceLeavesOneReport()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        Assert.True((await RunNewestFreezeJobAsync(web, bank.OrgId)).Succeeded);
        Assert.True((await RunNewestFreezeJobAsync(web, bank.OrgId)).Succeeded);

        var list = await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token));
        Assert.Single(list.GetProperty("ready").EnumerateArray());
        Assert.Equal(7, (await web.Service<KernelStore>().ReportFiles.FindAsync(TenantScope.ForTenant(bank.OrgId), _ => true)).Count);
    }

    [Fact]
    public async Task ASecondReportOfTheSameThingIsANewReportAndTheFirstIsUntouched()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var first = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);
        var firstSha = (await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports/{first}", bank.Token))).GetProperty("manifestSha256").GetString();

        var second = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);

        Assert.NotEqual(first, second);
        Assert.Equal(2, (await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token))).GetProperty("ready").GetArrayLength());
        Assert.Equal(firstSha, (await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports/{first}", bank.Token))).GetProperty("manifestSha256").GetString());
    }

    [Fact]
    public async Task AnotherOrganisationCannotListReadOrDownloadTheReport()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var other = await PartyAsync(web, "admin@other-bank.example", "Other Bank");
        var id = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{bank.OrgId}/reports", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}/files/report.csv", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{other.OrgId}/reports/{id}", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{other.OrgId}/reports/{id}/files/report.csv", other.Token)).StatusCode);
        var theirs = await Json(await Get(web, $"/api/organisations/{other.OrgId}/reports", other.Token));
        Assert.Empty(theirs.GetProperty("ready").EnumerateArray());
        Assert.Empty(theirs.GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task AnotherOrganisationsTrancheCannotBeReportedOn()
    {
        await using var web = await WebHarness.StartAsync();
        var (_, tranche, _) = await SeedBankAsync(web);
        var other = await PartyAsync(web, "admin@other-bank.example", "Other Bank");

        var response = await Post(web, $"/api/organisations/{other.OrgId}/reports", other.Token, new { kind = "tranche", subjectId = tranche.Id });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await web.Service<DataStore>().Jobs.ListAsync(TenantScope.ForTenant(other.OrgId), ReportService.FreezeJobType));
    }

    [Fact]
    public async Task AViewerCanReadReportsButNotAskForOne()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var viewerEmail = "viewer@nile-bank.example";
        var token = await web.Service<Zr3i.Kernel.Identity.InvitationService>().InviteAsync(TenantScope.ForTenant(bank.OrgId), "system", viewerEmail, ["viewer"]);
        var viewer = new Party(bank.OrgId, await web.SignInAsync(viewerEmail, await web.AcceptAsync(token)));

        Assert.Equal(HttpStatusCode.OK, (await Get(web, $"/api/organisations/{bank.OrgId}/reports", viewer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(web, $"/api/organisations/{bank.OrgId}/reports", viewer.Token, new { kind = "tranche", subjectId = tranche.Id })).StatusCode);
    }

    [Theory]
    [InlineData("bogus", null, null)]
    [InlineData("farm_risk", "x", "2026-27")]
    [InlineData("tranche", null, null)]
    public async Task AnInvalidRequestIsRefused(string kind, string? subjectId, string? season)
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, _, _) = await SeedBankAsync(web);

        var response = await Post(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token, new { kind, subjectId, season });
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, response.StatusCode.ToString());
    }

    [Fact]
    public async Task AFarmRiskReportNeedsAValidSeason()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, _, farm) = await SeedBankAsync(web);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token, new { kind = "farm_risk", subjectId = farm.Id, season = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token, new { kind = "farm_risk", subjectId = farm.Id })).StatusCode);
    }

    [Fact]
    public async Task WithoutABrowserTheJobFailsWithACodeAndNoReportIsMade()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });

        var outcome = await RunNewestFreezeJobAsync(web, bank.OrgId, new FakePdf(broken: true));
        Assert.False(outcome.Succeeded);
        Assert.Equal("browser_not_configured", outcome.ErrorCode);
        Assert.Empty((await Json(await Get(web, $"/api/organisations/{bank.OrgId}/reports", bank.Token))).GetProperty("ready").EnumerateArray());
    }

    [Fact]
    public async Task RequestingAndDownloadingAreAudited()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var id = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);
        await Get(web, $"/api/organisations/{bank.OrgId}/reports/{id}/files/report.csv", bank.Token);

        var actions = (await web.Service<KernelStore>().Audit.FindAsync(TenantScope.ForTenant(bank.OrgId), e => e.ObjectId == id)).Select(e => e.Action).ToList();
        Assert.Contains("report.requested", actions);
        Assert.Contains("report.frozen", actions);
        Assert.Contains("report.downloaded", actions);
    }

    [Fact]
    public async Task AReportFromASyntheticNamespaceCarriesTheBannerAndAnOrdinaryOneDoesNot()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var other = await SeedBankAsync(web, "admin@plain-bank.example", "Plain Bank");
        await web.Service<Zr3i.Kernel.Identity.OrganisationService>().SetSyntheticAsync(bank.OrgId, true);

        var synthetic = await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });
        await RunNewestFreezeJobAsync(web, bank.OrgId);
        var plain = await RequestAsync(web, other.Bank, new { kind = "tranche", subjectId = other.Tranche.Id });
        await RunNewestFreezeJobAsync(web, other.Bank.OrgId);

        async Task<string> Page(Party who, string id, string name) => await (await Get(web, $"/api/organisations/{who.OrgId}/reports/{id}/files/{name}", who.Token)).Content.ReadAsStringAsync();
        Assert.Contains("Synthetic namespace · Illustrative · No client data", await Page(bank, synthetic, "report.en.html"));
        Assert.Contains("مساحة بيانات تجريبية", await Page(bank, synthetic, "report.ar.html"));
        Assert.DoesNotContain("Synthetic namespace", await Page(other.Bank, plain, "report.en.html"));
    }

    [Fact]
    public async Task MeStatesWhetherTheOrganisationIsSynthetic()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, _, _) = await SeedBankAsync(web);
        async Task<bool> Flag() => (await Json(await Get(web, "/api/me", bank.Token))).GetProperty("organisations")[0].GetProperty("isSynthetic").GetBoolean();

        Assert.False(await Flag());
        await web.Service<Zr3i.Kernel.Identity.OrganisationService>().SetSyntheticAsync(bank.OrgId, true);
        Assert.True(await Flag());
    }

    private static async Task<string[]> InboxCodesAsync(WebHarness web, Party who) =>
        [.. (await Json(await Get(web, $"/api/organisations/{who.OrgId}/notifications", who.Token))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!)];

    [Fact]
    public async Task AFrozenReportTellsTheRequesterOnly()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        var token = await web.Service<Zr3i.Kernel.Identity.InvitationService>().InviteAsync(TenantScope.ForTenant(bank.OrgId), "system", "viewer@nile-bank.example", ["viewer"]);
        var viewer = new Party(bank.OrgId, await web.SignInAsync("viewer@nile-bank.example", await web.AcceptAsync(token)));
        await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });

        Assert.Empty(await InboxCodesAsync(web, bank));
        await RunNewestFreezeJobAsync(web, bank.OrgId);

        Assert.Equal(["report.ready"], await InboxCodesAsync(web, bank));
        Assert.Empty(await InboxCodesAsync(web, viewer));
    }

    [Fact]
    public async Task AReportThatCannotBeMadeTellsTheRequester()
    {
        await using var web = await WebHarness.StartAsync();
        var (bank, tranche, _) = await SeedBankAsync(web);
        await RequestAsync(web, bank, new { kind = "tranche", subjectId = tranche.Id });

        await RunNewestFreezeJobAsync(web, bank.OrgId, new FakePdf(broken: true));

        Assert.Equal(["report.failed"], await InboxCodesAsync(web, bank));
    }
}
