using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ConsentApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Party(string OrgId, string Token);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName, string type = "company", string role = "org-admin")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, role, orgName, type);
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    private static HttpRequestMessage Req(WebHarness web, HttpMethod m, string url, Party p, object? body = null) => web.Request(m, url, p.Token, body);

    private static object RequestBody(string farmerId, string granteeId, string[]? parcelIds = null) =>
        new { farmerId, granteeTenantId = granteeId, purpose = "Verify input finance", validUntil = DateTime.UtcNow.AddDays(90), parcelIds };

    /// <summary>The company's farmer Ali with a farm of three parcels, plus Omar's farm of one that must never be shared.</summary>
    private static async Task<(string AliId, Parcel[] Ali, Parcel Omar)> SeedAsync(WebHarness web, Party company)
    {
        var scope = TenantScope.ForTenant(company.OrgId);
        var farmers = web.Service<FarmerService>();
        var farms = web.Service<FarmService>();
        var ali = await farmers.CreateAsync(scope, "seed", new NewFarmer("Ali Mahmoud", Language: "ar"));
        var omar = await farmers.CreateAsync(scope, "seed", new NewFarmer("Omar Said"));
        var aliFarm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Ali's farm", FarmerId: ali.Id));
        var omarFarm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Omar's farm", FarmerId: omar.Id));
        var aliParcels = new List<Parcel>();
        for (var i = 0; i < 3; i++) aliParcels.Add(await farms.CreateParcelAsync(scope, "seed", new NewParcel(aliFarm.Id, $"Ali {i}")));
        return (ali.Id, [.. aliParcels], await farms.CreateParcelAsync(scope, "seed", new NewParcel(omarFarm.Id, "Omar 0")));
    }

    [Fact]
    public async Task FromRequestThroughTheFarmersLinkToTheBanksReadAndTheFarmersWithdrawal()
    {
        await using var web = await WebHarness.StartAsync();
        var company = await PartyAsync(web, "admin@agricash.example", "AgriCash");
        var bank = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var insurer = await PartyAsync(web, "admin@delta-ins.example", "Delta Insurance", "insurance");
        var (aliId, ali, omar) = await SeedAsync(web, company);

        // The company asks; nothing is shared yet.
        var requested = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{company.OrgId}/consent-requests", company, RequestBody(aliId, bank.OrgId))));
        var token = requested.GetProperty("consentToken").GetString()!;
        var shared = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared", bank)));
        Assert.Equal(0, shared.GetArrayLength());

        // The farmer opens the one-page link (no account, no sign-in) and sees exactly what they are agreeing to.
        var offer = await Json(await web.Http.GetAsync($"/api/consent/{token}"));
        Assert.Equal(("AgriCash", "Nile Bank", "Ali Mahmoud", "ar", "Verify input finance", "pending"),
            (offer.GetProperty("requester").GetString(), offer.GetProperty("grantee").GetString(), offer.GetProperty("farmer").GetString(),
             offer.GetProperty("language").GetString(), offer.GetProperty("purpose").GetString(), offer.GetProperty("status").GetString()));
        Assert.Contains("risk_results", offer.GetProperty("dataCategories").EnumerateArray().Select(c => c.GetString()));

        var agreed = await Json(await web.Http.PostAsJsonAsync($"/api/consent/{token}", new { accepted = true, language = "ar" }));
        Assert.True(agreed.GetProperty("granted").GetBoolean());
        var grantId = agreed.GetProperty("grantId").GetString()!;
        var withdrawToken = agreed.GetProperty("withdrawToken").GetString()!;

        // The bank now sees the grant, naming the grantor and the expiry, and reads through the gate.
        shared = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared", bank)));
        var item = shared.EnumerateArray().Single();
        Assert.Equal(("AgriCash", "Verify input finance"), (item.GetProperty("grantor").GetString(), item.GetProperty("purpose").GetString()));
        Assert.NotEqual(default, item.GetProperty("validUntil").GetDateTime());

        var url = $"/api/organisations/{bank.OrgId}/shared/{grantId}/parcels";
        var read = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, url, bank)));
        Assert.Equal(ali.Select(p => p.Id).Order(), read.GetProperty("parcels").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).Order());
        Assert.Equal(0, read.GetProperty("withheld").GetInt32());

        // Asking for parcels outside the grant returns only a count.
        var partial = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"{url}?ids={ali[0].Id},{omar.Id}", bank)));
        Assert.Equal([ali[0].Id], partial.GetProperty("parcels").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(1, partial.GetProperty("withheld").GetInt32());
        Assert.DoesNotContain(omar.Id, partial.ToString(), StringComparison.Ordinal);

        // Nobody else can use the grant.
        var byInsurer = await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{insurer.OrgId}/shared/{grantId}/parcels", insurer));
        Assert.Equal(HttpStatusCode.Forbidden, byInsurer.StatusCode);
        Assert.Equal("no_such_grant", (await Json(byInsurer)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{company.OrgId}/shared/{grantId}/parcels", bank))).StatusCode);   // bank pretending to be the company

        // The farmer withdraws with the link they were given; the next read is refused.
        var withdrawn = await Json(await web.Http.PostAsync($"/api/consent/withdraw/{withdrawToken}", null));
        Assert.Equal("withdrawn", withdrawn.GetProperty("status").GetString());
        var after = await web.Http.SendAsync(Req(web, HttpMethod.Get, url, bank));
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("withdrawn", (await Json(after)).GetProperty("reason").GetString());
        Assert.Equal(0, (await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared", bank)))).GetArrayLength());

        // Both organisations' audit trails show the reads and the refusal.
        var store = web.Service<KernelStore>();
        foreach (var tenant in new[] { company.OrgId, bank.OrgId })
        {
            var trail = (await store.Audit.FindAsync(TenantScope.ForTenant(tenant), _ => true)).Select(e => e.Action).ToList();
            Assert.Equal(2, trail.Count(a => a == "consent.read"));
            Assert.Contains("consent.refused", trail);
        }
    }

    [Fact]
    public async Task OnlyAnExplicitYesGrantsAnythingAndALinkIsUsedOnce()
    {
        await using var web = await WebHarness.StartAsync();
        var company = await PartyAsync(web, "admin@agricash.example", "AgriCash");
        var bank = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var (aliId, _, _) = await SeedAsync(web, company);
        async Task<string> NewToken() => (await Json(await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{company.OrgId}/consent-requests", company, RequestBody(aliId, bank.OrgId))))).GetProperty("consentToken").GetString()!;

        var empty = await NewToken();
        var noAnswer = await Json(await web.Http.PostAsJsonAsync($"/api/consent/{empty}", new { language = "en" }));          // "accepted" missing: not a yes
        Assert.False(noAnswer.GetProperty("granted").GetBoolean());

        var again = await web.Http.PostAsJsonAsync($"/api/consent/{empty}", new { accepted = true, language = "en" });
        Assert.Equal("already_answered", (await Json(again)).GetProperty("error").GetString());

        var badLanguage = await web.Http.PostAsJsonAsync($"/api/consent/{await NewToken()}", new { accepted = true, language = "fr" });
        Assert.Equal("invalid_language", (await Json(badLanguage)).GetProperty("error").GetString());

        Assert.Equal("invalid_token", (await Json(await web.Http.GetAsync("/api/consent/not-a-real-token"))).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.PostAsync("/api/consent/withdraw/not-a-real-token", null)).StatusCode);
        Assert.Equal(0, (await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared", bank)))).GetArrayLength());
    }

    [Fact]
    public async Task EachRoleCanDoOnlyWhatItsPermissionsAllow()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await PartyAsync(web, "admin@agricash.example", "AgriCash");
        var bank = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var (aliId, _, _) = await SeedAsync(web, admin);
        var invitations = web.Service<Zr3i.Kernel.Identity.InvitationService>();
        var analyst = new Party(admin.OrgId, await web.SignInAsync("analyst@agricash.example", await web.AcceptAsync(await invitations.InviteAsync(TenantScope.ForTenant(admin.OrgId), "system", "analyst@agricash.example", ["analyst"]))));
        var viewer = new Party(admin.OrgId, await web.SignInAsync("viewer@agricash.example", await web.AcceptAsync(await invitations.InviteAsync(TenantScope.ForTenant(admin.OrgId), "system", "viewer@agricash.example", ["viewer"]))));
        var body = RequestBody(aliId, bank.OrgId);
        var requestUrl = $"/api/organisations/{admin.OrgId}/consent-requests";

        // A viewer reads but cannot ask; an analyst can ask but cannot grant or withdraw; nobody outside the company can do anything.
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{admin.OrgId}/farmers", viewer))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Post, requestUrl, viewer, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await web.Http.SendAsync(Req(web, HttpMethod.Post, requestUrl, analyst, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{admin.OrgId}/consents", analyst, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{admin.OrgId}/consents/x/withdraw", analyst))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{admin.OrgId}/farmers", bank))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Post, requestUrl, bank, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PostAsJsonAsync(requestUrl, body)).StatusCode);

        // Even the company's own admin cannot grant for a farmer who has no account of their own: the right is the farmer's.
        var attempt = await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{admin.OrgId}/consents", admin, body));
        Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
        Assert.Equal("not_your_consent", (await Json(attempt)).GetProperty("error").GetString());
        Assert.Empty(await web.Service<KernelStore>().ConsentEvents.FindAsync(TenantScope.ForTenant(admin.OrgId), _ => true));
    }

    [Fact]
    public async Task AFarmerWithTheirOwnAccountGrantsAndWithdrawsFromIt()
    {
        await using var web = await WebHarness.StartAsync();
        var farmer = await PartyAsync(web, "ali@farm.example", "Abu Ali Farm", "farmer");
        var bank = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var company = await PartyAsync(web, "admin@agricash.example", "AgriCash");

        var scope = TenantScope.ForTenant(farmer.OrgId);
        var self = (await web.Service<FarmerService>().ListAsync(scope)).Single();
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("My farm", FarmerId: self.Id));
        await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, "Field 1"));

        var created = await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{farmer.OrgId}/consents", farmer, RequestBody(self.Id, bank.OrgId)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var grantId = (await Json(created)).GetProperty("grantId").GetString()!;

        var read = await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared/{grantId}/parcels", bank));
        Assert.Equal(1, (await Json(read)).GetProperty("parcels").GetArrayLength());

        // Another organisation's admin cannot withdraw it; the farmer can, and the bank is refused on the next read.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{farmer.OrgId}/consents/{grantId}/withdraw", company))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{bank.OrgId}/consents/{grantId}/withdraw", bank))).StatusCode);   // not the farmer's account
        var done = await web.Http.SendAsync(Req(web, HttpMethod.Post, $"/api/organisations/{farmer.OrgId}/consents/{grantId}/withdraw", farmer));
        Assert.Equal("withdrawn", (await Json(done)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{bank.OrgId}/shared/{grantId}/parcels", bank))).StatusCode);

        var list = await Json(await web.Http.SendAsync(Req(web, HttpMethod.Get, $"/api/organisations/{farmer.OrgId}/consents", farmer)));
        Assert.Equal(("owner", "withdrawn"), (list[0].GetProperty("role").GetString(), list[0].GetProperty("status").GetString()));
    }
}
