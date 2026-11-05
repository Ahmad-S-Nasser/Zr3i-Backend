using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>The read APIs behind Users and roles, the Audit log, Tenants and the Portfolio screen.</summary>
[Collection(MongoCollection.Name)]
public class AdminReadTests
{
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Party(string OrgId, string Token, string Email);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName, string type = "company", string role = "org-admin")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, role, orgName, type);
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)), email);
    }

    private static async Task<JsonElement> Get(WebHarness web, string url, string token) => await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, token)));

    [Fact]
    public async Task MembersListsPeopleRolesAndPendingInvitationsForOrganisationAdminsOnly()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        var other = await PartyAsync(web, "admin@other.example", "Other");
        var url = $"/api/organisations/{admin.OrgId}/members";

        var invited = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{admin.OrgId}/invitations", admin.Token, new { email = "analyst@delta.example", roleKeys = new[] { "analyst" } }));
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, other.Token))).StatusCode);

        var body = await Get(web, url, admin.Token);
        var member = body.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("email").GetString() == "admin@delta.example");
        Assert.Equal(("admin@delta.example", true, true), (member.GetProperty("email").GetString(), member.GetProperty("active").GetBoolean(), member.GetProperty("authenticatorEnrolled").GetBoolean()));
        Assert.Equal("org-admin", member.GetProperty("roles")[0].GetProperty("key").GetString());
        var pending = body.GetProperty("invitations").EnumerateArray().Single();
        Assert.Equal(("analyst@delta.example", "pending"), (pending.GetProperty("email").GetString(), pending.GetProperty("status").GetString()));
        Assert.Equal("analyst", pending.GetProperty("roles")[0].GetProperty("key").GetString());
        Assert.Contains("analyst", body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("key").GetString()));
        Assert.False(body.GetProperty("members")[0].TryGetProperty("passwordHash", out _));                       // never a secret

        // A deactivated membership is listed, as inactive (a second person, so the administrator is not locked out).
        var scope = TenantScope.ForTenant(admin.OrgId);
        var token = await web.Service<InvitationService>().InviteAsync(scope, "system", "viewer@delta.example", ["viewer"]);
        await web.AcceptAsync(token);
        var viewer = (await web.Service<KernelStore>().Users.FindAsync(PlatformTenant.Scope, u => u.Email == "viewer@delta.example")).Single();
        var membership = (await web.Service<KernelStore>().Memberships.FindAsync(scope, m => m.UserId == viewer.Id)).Single();
        membership.IsActive = false;
        await web.Service<KernelStore>().Memberships.ReplaceAsync(scope, membership);
        var listed = (await Get(web, url, admin.Token)).GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("email").GetString()!);
        Assert.Equal((false, true), (listed["viewer@delta.example"].GetProperty("active").GetBoolean(), listed["admin@delta.example"].GetProperty("active").GetBoolean()));

        // The other organisation sees only its own people.
        var theirs = await Get(web, $"/api/organisations/{other.OrgId}/members", other.Token);
        Assert.Equal("admin@other.example", theirs.GetProperty("members").EnumerateArray().Single().GetProperty("email").GetString());
        Assert.Equal(0, theirs.GetProperty("invitations").GetArrayLength());
    }

    [Fact]
    public async Task AnAnalystCannotReadMembersOrTheAuditLog()
    {
        await using var web = await WebHarness.StartAsync();
        var analyst = await PartyAsync(web, "analyst@delta.example", "Delta Agro", role: "analyst");

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{analyst.OrgId}/members", analyst.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{analyst.OrgId}/audit", analyst.Token))).StatusCode);
    }

    [Fact]
    public async Task TheAuditLogListsThisOrganisationsEventsNewestFirstWithTheActorsEmailAndFilters()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        var other = await PartyAsync(web, "admin@other.example", "Other");
        var scope = TenantScope.ForTenant(admin.OrgId);
        var audit = web.Service<AuditService>();
        var user = (await web.Service<KernelStore>().Users.FindAsync(PlatformTenant.Scope, u => u.Email == admin.Email)).Single();

        await audit.RecordAsync(scope, user.Id, "test.first", "thing", "1", purpose: "because");
        await Task.Delay(20);
        await audit.RecordAsync(scope, user.Id, "test.second", "thing", "2", outcome: "denied");
        await audit.RecordAsync(TenantScope.ForTenant(other.OrgId), "someone", "test.theirs", "thing", "9");

        var all = await Get(web, $"/api/organisations/{admin.OrgId}/audit?pageSize=500", admin.Token);
        var items = all.GetProperty("items").EnumerateArray().ToList();
        var actions = items.Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.DoesNotContain("test.theirs", actions);                                                              // another organisation's events never appear
        Assert.True(actions.IndexOf("test.second") < actions.IndexOf("test.first"));                                // newest first
        var second = items.Single(i => i.GetProperty("action").GetString() == "test.second");
        Assert.Equal(("admin@delta.example", "denied", "thing", "2"), (second.GetProperty("actorEmail").GetString(), second.GetProperty("outcome").GetString(), second.GetProperty("objectType").GetString(), second.GetProperty("objectId").GetString()));
        Assert.Equal("because", items.Single(i => i.GetProperty("action").GetString() == "test.first").GetProperty("purpose").GetString());

        // An event from 60 days ago is outside the default 30-day window, and a date range brings it back.
        var old = DateTime.UtcNow.AddDays(-60);
        await web.Service<KernelStore>().Audit.InsertAsync(scope, new AuditEvent { At = old, ActorUserId = user.Id, Action = "test.old", ObjectType = "thing", ObjectId = "0", Outcome = "ok" });
        Assert.DoesNotContain("test.old", (await Get(web, $"/api/organisations/{admin.OrgId}/audit?pageSize=500", admin.Token)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()));
        var ranged = await Get(web, $"/api/organisations/{admin.OrgId}/audit?from={old:yyyy-MM-dd}&to={old:yyyy-MM-dd}", admin.Token);
        Assert.Equal(["test.old"], ranged.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{admin.OrgId}/audit?from=not-a-date", admin.Token))).StatusCode);

        var filtered = await Get(web, $"/api/organisations/{admin.OrgId}/audit?action=test.sec", admin.Token);
        Assert.Equal(["test.second"], filtered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()));

        var paged = await Get(web, $"/api/organisations/{admin.OrgId}/audit?pageSize=1", admin.Token);
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(paged.GetProperty("nextCursor").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{admin.OrgId}/audit?pageSize=5000", admin.Token))).StatusCode);
    }

    [Fact]
    public async Task OnlyTheProviderCanListTenantsAndEachShowsItsTypeMembersAndFarms()
    {
        await using var web = await WebHarness.StartAsync();
        var token = await web.Service<PlatformService>().BootstrapAsync("root@zr3i.example");
        var root = new Party(PlatformTenant.Id, await web.SignInAsync("root@zr3i.example", await web.AcceptAsync(token!)), "root@zr3i.example");
        var company = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        await web.Service<FarmService>().CreateFarmAsync(TenantScope.ForTenant(company.OrgId), "seed", new NewFarm("F-1"));
        await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/platform/organisations", company.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync("/api/platform/organisations")).StatusCode);

        var list = (await Get(web, "/api/platform/organisations", root.Token)).EnumerateArray().ToDictionary(o => o.GetProperty("name").GetString()!);
        Assert.DoesNotContain("Zr3i", list.Keys);                                                                    // the provider is not a customer tenant
        var delta = list["Delta Agro"];
        Assert.Equal(("company", 1, 1), (delta.GetProperty("accountType").GetString(), delta.GetProperty("members").GetInt32(), delta.GetProperty("farms").GetInt32()));
        Assert.Equal("bank", list["Nile Bank"].GetProperty("accountType").GetString());
        Assert.Contains("risk", delta.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task ThePortfolioSummaryCountsFarmsParcelsAndMeasuredAreaPerRegionAndFiltersTheFarmListByRegion()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        var other = await PartyAsync(web, "admin@other.example", "Other");
        var scope = TenantScope.ForTenant(org.OrgId);
        var farms = web.Service<FarmService>();

        var a = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-A", Governorate: "Kafr El Sheikh"));
        var b = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-B", Governorate: "Kafr El Sheikh"));
        var c = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-C", Governorate: "Gharbia"));
        await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-D"));
        async Task Parcel(Farm f, string label, double? side)
        {
            var p = await farms.CreateParcelAsync(scope, "seed", new NewParcel(f.Id, label));
            if (side is not null) await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(side.Value), new BoundaryProvenance("survey", "s", Captured, 1));
        }

        await Parcel(a, "a1", 100);
        await Parcel(a, "a2", 200);
        await Parcel(c, "c1", 150);
        await Parcel(c, "c2", null);
        await farms.CreateFarmAsync(TenantScope.ForTenant(other.OrgId), "seed", new NewFarm("Other farm", Governorate: "Giza"));
        _ = b;

        var url = $"/api/organisations/{org.OrgId}/portfolio";
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, other.Token))).StatusCode);

        var s = await Get(web, url, org.Token);
        Assert.Equal((4, 4), (s.GetProperty("farms").GetInt32(), s.GetProperty("parcels").GetInt32()));
        Assert.Equal((100.0 * 100 + 200.0 * 200 + 150.0 * 150) / 4200.83, s.GetProperty("areaFeddan").GetDouble(), 2);        // parcels without a boundary add no area
        var regions = s.GetProperty("regions").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("farms").GetInt32());
        Assert.Equal((2, 1), (regions["Kafr El Sheikh"], regions["Gharbia"]));
        Assert.DoesNotContain("Giza", regions.Keys);                                                                         // another organisation's region
        Assert.Equal("farm_cohort_not_defined", s.GetProperty("bandsReason").GetString());

        var kafr = await Get(web, url + "?governorate=Kafr%20El%20Sheikh", org.Token);
        Assert.Equal((2, 2, "Kafr El Sheikh"), (kafr.GetProperty("farms").GetInt32(), kafr.GetProperty("parcels").GetInt32(), kafr.GetProperty("governorate").GetString()));
        Assert.Equal((100.0 * 100 + 200.0 * 200) / 4200.83, kafr.GetProperty("areaFeddan").GetDouble(), 2);

        var list = await Get(web, $"/api/organisations/{org.OrgId}/farms?governorate=Gharbia", org.Token);
        Assert.Equal(["F-C"], list.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
    }
}
