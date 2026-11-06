using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Admin;
using Zr3i.Kernel.Chips;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Reports;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Usage across organisations for revenue share: counts only, per organisation and in a period, visible to the provider's admin and nobody else.</summary>
[Collection(MongoCollection.Name)]
public class UsageTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));

    private sealed record Person(string OrgId, string UserId, string Token);

    private static async Task<Person> JoinAsync(WebHarness web, string orgId, string email, string role)
    {
        var scope = orgId == PlatformTenant.Id ? PlatformTenant.Scope : TenantScope.ForTenant(orgId);
        var invite = await web.Service<InvitationService>().InviteAsync(scope, "system", email, [role]);
        var token = await web.SignInAsync(email, await web.AcceptAsync(invite));
        var me = await Json(await Get(web, "/api/me", token));
        return new Person(orgId, me.GetProperty("id").GetString()!, token);
    }

    private sealed record World(Person Admin, Person Analyst, Person CompanyAdmin, Person BankAdmin, string CompanyId, string BankId, string GrantId, string FarmerId);

    private static string Url(DateTime? from = null, DateTime? to = null, string extra = "") =>
        $"/api/platform/usage?from={Uri.EscapeDataString((from ?? DateTime.UtcNow.AddDays(-1)).ToString("O"))}&to={Uri.EscapeDataString((to ?? DateTime.UtcNow.AddDays(1)).ToString("O"))}{extra}";

    /// <summary>The company holds two parcels, made 3 readings (2 usable, 2 scenes), 2 pictures (1,000 and 2,000 bytes), 2 reports, 3 member downloads, one committed import (5 created, 2 updated), and granted the bank access and withdrew it. The bank read the shared data twice.</summary>
    private static async Task<World> SeedAsync(WebHarness web, string companyName = "Delta Agro")
    {
        await web.Service<OrganisationService>().EnsureProviderAsync();
        var admin = await JoinAsync(web, PlatformTenant.Id, "admin@zr3i.example", "org-admin");
        var analyst = await JoinAsync(web, PlatformTenant.Id, "analyst@zr3i.example", "analyst");
        var company = await web.Service<OrganisationService>().CreateAsync(companyName, "company");
        var bank = await web.Service<OrganisationService>().CreateAsync("Nile Bank", "bank");
        var companyAdmin = await JoinAsync(web, company.Id, "admin@delta.example", "org-admin");
        var bankAdmin = await JoinAsync(web, bank.Id, "admin@nile.example", "org-admin");
        var scope = TenantScope.ForTenant(company.Id);
        var store = web.Service<KernelStore>();
        var now = DateTime.UtcNow;

        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("Delta Estate"));
        var parcels = new List<Parcel>();
        for (var i = 0; i < 2; i++)
        {
            var p = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "ZR-0" + i));
            await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(150, 303800 + i * 400, 3409200), new BoundaryProvenance("survey", "s", now, 1));
            parcels.Add(p);
        }

        foreach (var (parcel, scene, readable) in new[] { (0, "S-1", true), (1, "S-1", true), (0, "S-2", false) })
            await store.ParcelObservations.InsertAsync(scope, new ParcelObservation { ParcelId = parcels[parcel].Id, BoundaryId = "b", SceneId = scene, Readable = readable, IngestedAt = now });
        foreach (var bytes in new[] { 1000L, 2000L })
            await store.ParcelChips.InsertAsync(scope, new ParcelChip { ParcelId = parcels[0].Id, BoundaryId = "b", SceneId = "S-1", Kind = bytes == 1000 ? "ndvi" : "true_colour", Bytes = bytes, Path = "x", Sha256 = "AB", CreatedAt = now });
        for (var i = 0; i < 2; i++)
            await store.FrozenReports.InsertAsync(scope, new FrozenReport { Kind = "tranche", SubjectName = "T" + i, SnapshotJson = "{}", SnapshotSha256 = "x", CreatedAt = now });
        for (var i = 0; i < 3; i++) await web.Service<Zr3i.Kernel.Audit.AuditService>().RecordAsync(scope, companyAdmin.UserId, "report.downloaded", "report", "r" + i);
        await store.ImportBatches.InsertAsync(scope, new ImportBatch { Status = ImportStatus.Committed, Created = 5, Updated = 2, CreatedAt = now });

        var farmer = await web.Service<FarmerService>().CreateAsync(scope, companyAdmin.UserId, new NewFarmer("Ali Mahmoud"));
        var consent = web.Service<ConsentService>();
        var (_, token) = await consent.RequestAsync(scope, companyAdmin.UserId, new GrantTerms(farmer.Id, bank.Id, "Verify finance", now.AddDays(90)));
        var granted = await consent.RespondAsync(token, true, "en");
        await consent.WithdrawByTokenAsync(granted.WithdrawToken!);
        var audit = web.Service<Zr3i.Kernel.Audit.AuditService>();
        await audit.RecordAsync(TenantScope.ForTenant(bank.Id), bankAdmin.UserId, "consent.read", "consent_grant", granted.GrantId!);
        await audit.RecordAsync(TenantScope.ForTenant(bank.Id), bankAdmin.UserId, "consent.read", "consent_grant", granted.GrantId!);
        await audit.RecordAsync(TenantScope.ForTenant(bank.Id), admin.UserId, "consent.read", "consent_grant", granted.GrantId!);     // the provider looking in: not usage
        return new World(admin, analyst, companyAdmin, bankAdmin, company.Id, bank.Id, granted.GrantId!, farmer.Id);
    }

    private static JsonElement Row(JsonElement usage, string orgId) => usage.GetProperty("organisations").EnumerateArray().Single(o => o.GetProperty("id").GetString() == orgId);

    [Fact]
    public async Task EachOrganisationsFiguresMatchWhatItHoldsAndDid()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var usage = await Json(await Get(web, Url(), w.Admin.Token));
        var company = Row(usage, w.CompanyId);

        Assert.Equal(2, company.GetProperty("parcels").GetInt32());
        Assert.Equal((await web.Service<PortfolioService>().SummaryAsync(TenantScope.ForTenant(w.CompanyId), null)).AreaFeddan, company.GetProperty("areaFeddan").GetDouble(), 6);
        Assert.True(company.GetProperty("areaFeddan").GetDouble() > 8);
        Assert.Equal((3, 2, 2), (company.GetProperty("readings").GetInt32(), company.GetProperty("usableReadings").GetInt32(), company.GetProperty("scenes").GetInt32()));
        Assert.Equal((2, 3000L), (company.GetProperty("pictures").GetInt32(), company.GetProperty("pictureBytes").GetInt64()));
        Assert.Equal((2, 3), (company.GetProperty("reports").GetInt32(), company.GetProperty("downloads").GetInt32()));
        Assert.Equal((1, 7), (company.GetProperty("importBatches").GetInt32(), company.GetProperty("importedRows").GetInt32()));
        Assert.Equal((1, 1, 0), (company.GetProperty("grants").GetInt32(), company.GetProperty("withdrawals").GetInt32(), company.GetProperty("sharedReads").GetInt32()));

        var bank = Row(usage, w.BankId);
        Assert.Equal((0, 0, 0, 0), (bank.GetProperty("parcels").GetInt32(), bank.GetProperty("readings").GetInt32(), bank.GetProperty("pictures").GetInt32(), bank.GetProperty("reports").GetInt32()));
        Assert.Equal((0, 0, 2), (bank.GetProperty("grants").GetInt32(), bank.GetProperty("withdrawals").GetInt32(), bank.GetProperty("sharedReads").GetInt32()));
    }

    [Fact]
    public async Task TotalsAreTheSumOfTheOrganisations()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var usage = await Json(await Get(web, Url(), w.Admin.Token));

        foreach (var key in new[] { "parcels", "readings", "pictures", "pictureBytes", "reports", "downloads", "importedRows", "grants", "withdrawals", "sharedReads" })
            Assert.Equal(usage.GetProperty("organisations").EnumerateArray().Sum(o => o.GetProperty(key).GetInt64()), usage.GetProperty("totals").GetProperty(key).GetInt64());
    }

    [Fact]
    public async Task AnotherPeriodCountsOnlyWhatHappenedInIt()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var earlier = await Json(await Get(web, Url(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-20)), w.Admin.Token));
        var company = Row(earlier, w.CompanyId);

        Assert.Equal(2, company.GetProperty("parcels").GetInt32());   // what it holds now is not a period figure
        foreach (var key in new[] { "readings", "usableReadings", "scenes", "pictures", "reports", "downloads", "importBatches", "importedRows", "grants", "withdrawals" })
            Assert.Equal(0, company.GetProperty(key).GetInt32());
        Assert.Equal(0, Row(earlier, w.BankId).GetProperty("sharedReads").GetInt32());
    }

    [Fact]
    public async Task OnlyAMemberOfTheOrganisationCountsAsUsageAndAProvidersViewDoesNot()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);
        // The provider opens a report download through oversight: the audit trail records it, usage must not.
        await web.Service<Zr3i.Kernel.Audit.AuditService>().RecordAsync(TenantScope.ForTenant(w.CompanyId), w.Admin.UserId, "report.downloaded", "report", "seen-by-provider");

        var company = Row(await Json(await Get(web, Url(), w.Admin.Token)), w.CompanyId);

        Assert.Equal(3, company.GetProperty("downloads").GetInt32());
    }

    // ---- who may ask, and what is asked ----

    [Fact]
    public async Task OnlyTheProviderAdminMaySeeUsage()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        Assert.Equal(HttpStatusCode.OK, (await Get(web, Url(), w.Admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, Url(), w.Analyst.Token)).StatusCode);        // "*:*:read" is a partial wildcard
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, Url(), w.CompanyAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, Url(extra: "&format=csv"), w.BankAdmin.Token)).StatusCode);
    }

    [Fact]
    public async Task AnInvalidPeriodIsRefused()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/platform/usage", w.Admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, Url(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1)), w.Admin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, Url(DateTime.UtcNow.AddDays(-500), DateTime.UtcNow), w.Admin.Token)).StatusCode);
    }

    [Fact]
    public async Task UsageCarriesNoRecordsNamesNoFarmsAndNoPeople()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var text = await (await Get(web, Url(), w.Admin.Token)).Content.ReadAsStringAsync();
        var csv = await (await Get(web, Url(extra: "&format=csv"), w.Admin.Token)).Content.ReadAsStringAsync();

        foreach (var secret in new[] { "Delta Estate", "ZR-00", "Ali Mahmoud", "admin@delta.example", "Verify finance", "T0" })
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(secret, csv);
        }
    }

    [Fact]
    public async Task TheCsvHasOneRowPerOrganisationATotalRowAndCellsThatCannotRunAsFormulas()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web, "=HYPERLINK(\"http://x\")");

        var response = await Get(web, Url(extra: "&format=csv"), w.Admin.Token);
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').TrimEnd().Split("\r\n");

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("id,name,account_type,parcels,area_feddan,readings,usable_readings,scenes,pictures,picture_bytes,reports,downloads,import_batches,imported_rows,grants,withdrawals,shared_reads", lines[0]);
        Assert.Equal(1 + 2 + 1, lines.Length);     // header, two organisations, total
        Assert.Contains(lines, l => l.Contains("'=HYPERLINK") && !l.Contains(",=HYPERLINK"));
        Assert.StartsWith(",TOTAL,", lines[^1]);
        Assert.Contains(",3000,", lines.Single(l => l.Contains(w.CompanyId)));
    }

    [Fact]
    public async Task TheDirectoryCarriesWhatIsNeededToOpenAnOrganisationReadOnly()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var list = await Json(await Get(web, "/api/platform/organisations", w.Admin.Token));
        var company = list.EnumerateArray().Single(o => o.GetProperty("id").GetString() == w.CompanyId);

        Assert.Equal("portfolio", company.GetProperty("homeView").GetString());
        Assert.Contains("parcels", company.GetProperty("navigation").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Company", company.GetProperty("accountTypeLabelEn").GetString());
        Assert.False(company.GetProperty("isSynthetic").GetBoolean());
    }
}
