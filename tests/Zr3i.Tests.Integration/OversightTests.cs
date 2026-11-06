using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Provider oversight (ADR 0020): the Zr3i admin may read any organisation, never write, never without a trace, and nobody else gets this.</summary>
[Collection(MongoCollection.Name)]
public class OversightTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));
    private static Task<HttpResponseMessage> Post(WebHarness web, string url, string token, object? body = null) => web.Http.SendAsync(web.Request(HttpMethod.Post, url, token, body ?? new { }));

    private sealed record Person(string OrgId, string Email, string UserId, string Token);

    private static async Task<Person> JoinAsync(WebHarness web, string orgId, string email, string role)
    {
        var scope = orgId == PlatformTenant.Id ? PlatformTenant.Scope : TenantScope.ForTenant(orgId);
        var invite = await web.Service<InvitationService>().InviteAsync(scope, "system", email, [role]);
        var token = await web.SignInAsync(email, await web.AcceptAsync(invite));
        var me = await Json(await Get(web, "/api/me", token));
        return new Person(orgId, email, me.GetProperty("id").GetString()!, token);
    }

    private sealed record World(Person ProviderAdmin, Person ProviderAnalyst, Person CompanyAdmin, Person CompanyViewer, Person BankAdmin, string CompanyId, string BankId);

    private static async Task<World> SeedAsync(WebHarness web)
    {
        await web.Service<OrganisationService>().EnsureProviderAsync();
        var admin = await JoinAsync(web, PlatformTenant.Id, "admin@zr3i.example", "org-admin");
        var analyst = await JoinAsync(web, PlatformTenant.Id, "analyst@zr3i.example", "analyst");
        var company = await web.Service<OrganisationService>().CreateAsync("Delta Agro", "company");
        var bank = await web.Service<OrganisationService>().CreateAsync("Nile Bank", "bank");
        var companyAdmin = await JoinAsync(web, company.Id, "admin@delta.example", "org-admin");
        var viewer = await JoinAsync(web, company.Id, "viewer@delta.example", "viewer");
        var bankAdmin = await JoinAsync(web, bank.Id, "admin@nile.example", "org-admin");
        var scope = TenantScope.ForTenant(company.Id);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("Delta Estate"));
        await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "ZR-01"));
        return new World(admin, analyst, companyAdmin, viewer, bankAdmin, company.Id, bank.Id);
    }

    private static async Task<IReadOnlyList<AuditEvent>> AuditOf(WebHarness web, TenantScope scope) =>
        await web.Service<KernelStore>().Audit.FindAsync(scope, e => e.Action == "oversight.read");

    // ---- what the provider can read ----

    [Theory]
    [InlineData("farms")]
    [InlineData("parcels")]
    [InlineData("reports")]
    [InlineData("imports")]
    [InlineData("tranches")]
    [InlineData("event-windows")]
    [InlineData("members")]
    [InlineData("audit")]
    [InlineData("notifications")]
    [InlineData("imagery/status")]
    [InlineData("search?q=delta")]
    public async Task TheProviderAdminCanReadAnOrganisationItIsNotAMemberOf(string route)
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var response = await Get(web, $"/api/organisations/{w.CompanyId}/{route}", w.ProviderAdmin.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhatTheProviderSeesIsTheOrganisationsOwnData()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        var farms = await Json(await Get(web, $"/api/organisations/{w.CompanyId}/farms?pageSize=10", w.ProviderAdmin.Token));
        var other = await Json(await Get(web, $"/api/organisations/{w.BankId}/farms?pageSize=10", w.ProviderAdmin.Token));

        Assert.Equal(["Delta Estate"], farms.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(0, other.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task NoResponseCarriesASecret()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        foreach (var route in new[] { "members", "audit", "farms", "search?q=zr" })
        {
            var text = (await (await Get(web, $"/api/organisations/{w.CompanyId}/{route}", w.ProviderAdmin.Token)).Content.ReadAsStringAsync()).ToLowerInvariant();
            Assert.DoesNotContain("passwordhash", text);
            Assert.DoesNotContain("totpsecret", text);
            Assert.DoesNotContain("resettokenhash", text);
        }
    }

    // ---- what the provider cannot do ----

    [Fact]
    public async Task EveryWriteFromAnOversightSessionIsRefusedAndChangesNothing()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);
        var store = web.Service<KernelStore>();
        var scope = TenantScope.ForTenant(w.CompanyId);
        var token = w.ProviderAdmin.Token;
        var farmsBefore = await store.Farms.CountAsync(scope, _ => true);
        var membersBefore = await store.Memberships.CountAsync(scope, _ => true);

        var attempts = new (string Url, object Body)[]
        {
            ($"/api/organisations/{w.CompanyId}/tranches", new { name = "T", season = "2026-27", farmIds = Array.Empty<string>() }),
            ($"/api/organisations/{w.CompanyId}/reports", new { kind = "farmer_results", season = "2026-27" }),
            ($"/api/organisations/{w.CompanyId}/notifications/read", new { all = true }),
            ($"/api/organisations/{w.CompanyId}/invitations", new { email = "x@y.example", roleKeys = new[] { "org-admin" } }),
            ($"/api/organisations/{w.CompanyId}/members/{w.CompanyViewer.UserId}/reset-authenticator", new { }),
            ($"/api/organisations/{w.CompanyId}/imagery/discover", new { from = DateTime.UtcNow.AddDays(-5), to = DateTime.UtcNow }),
            ($"/api/organisations/{w.CompanyId}/imports?fileName=a.csv", new { }),
        };
        foreach (var (url, body) in attempts) Assert.Equal(HttpStatusCode.Forbidden, (await Post(web, url, token, body)).StatusCode);

        Assert.Equal(farmsBefore, await store.Farms.CountAsync(scope, _ => true));
        Assert.Equal(membersBefore, await store.Memberships.CountAsync(scope, _ => true));
        Assert.Empty(await web.Service<DataStore>().Jobs.ListAsync(scope));
        Assert.True((await web.Service<KernelStore>().Users.GetAsync(PlatformTenant.Scope, w.CompanyViewer.UserId))!.NeedsAuthenticator == false);
    }

    [Fact]
    public async Task OnlyAReadOfAReadOrManagePermissionCountsAsOversight()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);
        var auth = web.Service<AuthorizationService>();
        var admin = w.ProviderAdmin.UserId;

        Assert.True(await auth.OversightAllowedAsync(admin, w.CompanyId, "portfolio:farms:read", true));
        Assert.True(await auth.OversightAllowedAsync(admin, w.CompanyId, "kernel:audit:manage", true));
        Assert.False(await auth.OversightAllowedAsync(admin, w.CompanyId, "portfolio:farms:write", true));
        Assert.False(await auth.OversightAllowedAsync(admin, w.CompanyId, "portfolio:farms:read", false));
        Assert.False(await auth.OversightAllowedAsync(admin, "no-such-organisation", "portfolio:farms:read", true));
        Assert.False(await auth.OversightAllowedAsync(admin, PlatformTenant.Id, "portfolio:farms:read", true));
    }

    // ---- who does not get it ----

    [Fact]
    public async Task ProviderStaffWithoutTheFullPermissionAndEveryCustomerAreRefused()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);
        var url = $"/api/organisations/{w.CompanyId}/farms?pageSize=10";

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, url, w.ProviderAnalyst.Token)).StatusCode);   // "*:*:read" is a partial wildcard
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, url, w.BankAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{w.BankId}/farms?pageSize=10", w.CompanyAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{w.BankId}/members", w.CompanyAdmin.Token)).StatusCode);
        Assert.Empty(await AuditOf(web, TenantScope.ForTenant(w.CompanyId)));
    }

    [Fact]
    public async Task AnOrganisationsOwnMemberNeverGeneratesAnOversightEvent()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        await Get(web, $"/api/organisations/{w.CompanyId}/farms?pageSize=10", w.CompanyAdmin.Token);
        await Get(web, $"/api/organisations/{w.CompanyId}/notifications", w.CompanyViewer.Token);

        Assert.Empty(await AuditOf(web, TenantScope.ForTenant(w.CompanyId)));
    }

    // ---- the trace ----

    [Fact]
    public async Task EveryOversightRequestIsRecordedInTheViewedOrganisationsLogAndInThePlatformLog()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        await Get(web, $"/api/organisations/{w.CompanyId}/farms?pageSize=10", w.ProviderAdmin.Token);
        await Get(web, $"/api/organisations/{w.CompanyId}/members", w.ProviderAdmin.Token);
        await Get(web, $"/api/organisations/{w.CompanyId}/search?q=zr", w.ProviderAdmin.Token);

        var theirs = await AuditOf(web, TenantScope.ForTenant(w.CompanyId));
        var platform = await web.Service<KernelStore>().Audit.FindAsync(PlatformTenant.Scope, e => e.Action == "oversight.read" && e.ObjectId == w.CompanyId);
        Assert.Equal(3, theirs.Count);
        Assert.Equal(3, platform.Count);
        Assert.All(theirs, e => Assert.Equal(w.ProviderAdmin.UserId, e.ActorUserId));
        Assert.Equal(["kernel:self:read", "kernel:users:manage", "portfolio:farms:read"], theirs.Select(e => e.Purpose!).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(await AuditOf(web, TenantScope.ForTenant(w.BankId)));
    }

    [Fact]
    public async Task TheOrganisationsOwnAdminCanSeeThatTheProviderLookedAndWho()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);
        await Get(web, $"/api/organisations/{w.CompanyId}/farms?pageSize=10", w.ProviderAdmin.Token);

        var log = await Json(await Get(web, $"/api/organisations/{w.CompanyId}/audit", w.CompanyAdmin.Token));
        var events = log.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("action").GetString() == "oversight.read").ToList();

        var one = Assert.Single(events);
        Assert.Equal("admin@zr3i.example", one.GetProperty("actorEmail").GetString());
    }

    [Fact]
    public async Task ARefusedAttemptIsStillRecordedAsAnAccessDenial()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await SeedAsync(web);

        await Get(web, $"/api/organisations/{w.CompanyId}/farms?pageSize=10", w.BankAdmin.Token);

        var denials = await web.Service<KernelStore>().Audit.FindAsync(TenantScope.ForTenant(w.CompanyId), e => e.Action == "access.denied");
        Assert.Single(denials);
    }
}
