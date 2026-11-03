using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Kernel.Accounts;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AccessRequestApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static object BankRequest(string email = "head@nile-bank.example") => new
    {
        accountType = "bank", organisationName = "Nile Bank", contactName = "Mona Hassan", email,
        purpose = "Verify declared area before disbursing input finance.", language = "en",
        fields = new Dictionary<string, string> { ["commercial_register_number"] = "CR-123456" },
    };

    /// <summary>Starts the API with a Zr3i administrator already signed in. Returns the admin's bearer token.</summary>
    private static async Task<string> ProviderAdminAsync(WebHarness web)
    {
        var token = await web.Service<PlatformService>().BootstrapAsync("founder@zr3i.example");
        return await web.SignInAsync("founder@zr3i.example", await web.AcceptAsync(token!));
    }

    [Fact]
    public async Task TheRequestFormIsDescribedByThePresetsAndExcludesStaffAccounts()
    {
        await using var web = await WebHarness.StartAsync();

        var types = await Json(await web.Http.GetAsync("/api/account-types"));

        var keys = types.EnumerateArray().Select(t => t.GetProperty("key").GetString()).Order().ToList();
        Assert.Equal(["bank", "company", "farmer", "insurance"], keys);
        var farmer = types.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "farmer");
        Assert.Equal("mobile_number", farmer.GetProperty("signupFields")[0].GetProperty("key").GetString());
        Assert.Equal("رقم الهاتف المحمول", farmer.GetProperty("signupFields")[0].GetProperty("labelAr").GetString());
    }

    [Fact]
    public async Task AVisitorRequestsAccessAndProblemsComeBackAsCodes()
    {
        await using var web = await WebHarness.StartAsync();

        var ok = await web.Http.PostAsJsonAsync("/api/access-requests", BankRequest());
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("pending", (await Json(ok)).GetProperty("status").GetString());

        var dup = await web.Http.PostAsJsonAsync("/api/access-requests", BankRequest());
        Assert.Equal("duplicate_pending", (await Json(dup)).GetProperty("error").GetString());

        var staff = await web.Http.PostAsJsonAsync("/api/access-requests", new { accountType = "zr3i-admin", organisationName = "x", contactName = "x", email = "x@x.example", purpose = "x", language = "en" });
        Assert.Equal("signup_not_allowed", (await Json(staff)).GetProperty("error").GetString());

        var missing = await web.Http.PostAsJsonAsync("/api/access-requests", new { accountType = "company", organisationName = "x", contactName = "x", email = "y@x.example", purpose = "x", language = "ar" });
        Assert.Contains("field_required:commercial_register_number", (await Json(missing)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task OnlyZr3iAdministratorsCanSeeAndDecideRequests()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await ProviderAdminAsync(web);
        var (_, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var ordinary = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var created = await Json(await web.Http.PostAsJsonAsync("/api/access-requests", BankRequest()));
        var id = created.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync("/api/platform/access-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/platform/access-requests", ordinary))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/platform/access-requests/{id}/approve", ordinary))).StatusCode);

        var pending = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/platform/access-requests?status=pending", admin)));
        Assert.Equal(id, pending.EnumerateArray().Single().GetProperty("id").GetString());
    }

    [Fact]
    public async Task ApprovalGivesTheNewOrganisationAnAdminWhoCanSignInAndSeeTheirOrganisation()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await ProviderAdminAsync(web);
        var id = (await Json(await web.Http.PostAsJsonAsync("/api/access-requests", BankRequest()))).GetProperty("id").GetString();

        var approved = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/platform/access-requests/{id}/approve", admin)));
        Assert.Equal("approved", approved.GetProperty("request").GetProperty("status").GetString());
        var token = approved.GetProperty("inviteToken").GetString()!;

        var bankAdmin = await web.SignInAsync("head@nile-bank.example", await web.AcceptAsync(token));
        var me = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", bankAdmin)));
        var org = me.GetProperty("organisations").EnumerateArray().Single();
        Assert.Equal(("Nile Bank", "bank"), (org.GetProperty("name").GetString(), org.GetProperty("accountType").GetString()));

        var again = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/platform/access-requests/{id}/approve", admin));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("not_pending", (await Json(again)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ARejectedRequestKeepsItsReasonAndCreatesNoOrganisation()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await ProviderAdminAsync(web);
        var id = (await Json(await web.Http.PostAsJsonAsync("/api/access-requests", BankRequest()))).GetProperty("id").GetString();

        var rejected = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/platform/access-requests/{id}/reject", admin, new { reason = "unverified_register_number" })));

        Assert.Equal(("rejected", "unverified_register_number"), (rejected.GetProperty("status").GetString(), rejected.GetProperty("decisionReason").GetString()));
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("organisationId").ValueKind);
    }
}
