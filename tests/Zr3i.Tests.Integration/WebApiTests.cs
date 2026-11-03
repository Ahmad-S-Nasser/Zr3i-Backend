using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class WebApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task HealthAndReadyAnswerWithoutSignIn()
    {
        await using var web = await WebHarness.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task InvitationToSignedInProfileToSignOut()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, invite) = await web.OrgWithInvitationAsync("Admin@Delta.example");

        var secret = await web.AcceptAsync(invite, "ar");
        var token = await web.SignInAsync("admin@delta.example", secret);

        var me = await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await Json(me);
        Assert.Equal("admin@delta.example", body.GetProperty("email").GetString());
        Assert.Equal("ar", body.GetProperty("language").GetString());
        var org = body.GetProperty("organisations").EnumerateArray().Single();
        Assert.Equal(orgId, org.GetProperty("id").GetString());
        Assert.Equal("Delta Agro", org.GetProperty("name").GetString());
        Assert.Contains("*:*:*", org.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/auth/sign-out", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token))).StatusCode);
    }

    [Fact]
    public async Task ProtectedRoutesRefuseMissingAndForgedTokens()
    {
        await using var web = await WebHarness.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", "not-a-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/auth/sign-out", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(
            web.Request(HttpMethod.Post, "/api/organisations/any/invitations", null, new { email = "a@b.test", roleKeys = new[] { "viewer" } }))).StatusCode);
    }

    [Fact]
    public async Task SignInFailuresGiveTheSameAnswerWhateverTheReason()
    {
        await using var web = await WebHarness.StartAsync();
        var (_, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var secret = await web.AcceptAsync(invite);

        var wrongPassword = await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = "admin@delta.example", password = "wrong password here" });
        var unknownEmail = await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = "nobody@delta.example", password = WebHarness.Password });
        var badCode = await web.Http.PostAsJsonAsync("/api/auth/verify", new { challenge = "x.y", code = "123456" });

        foreach (var r in new[] { wrongPassword, unknownEmail, badCode })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("invalid_credentials", (await Json(r)).GetProperty("error").GetString());
        }

        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownEmail.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = "", password = "" })).StatusCode);
    }

    [Fact]
    public async Task InvitationErrorsNameOnlyWhatTheTokenHolderCanFix()
    {
        await using var web = await WebHarness.StartAsync();
        var (_, invite) = await web.OrgWithInvitationAsync("new@delta.example", "viewer");
        var secret = Totp.NewSecret();
        var code = Totp.CodeAt(secret, DateTimeOffset.UtcNow);

        async Task<(HttpStatusCode, string?)> Accept(string token, string password, string totp)
        {
            var r = await web.Http.PostAsJsonAsync("/api/invitations/accept",
                new { token, password, language = "en", totpSecret = secret, totpCode = totp });
            var body = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, body.Contains("error", StringComparison.Ordinal) ? JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() : null);
        }

        Assert.Equal((HttpStatusCode.BadRequest, "weak_password"), await Accept(invite, "short", code));
        Assert.Equal((HttpStatusCode.BadRequest, "bad_code"), await Accept(invite, WebHarness.Password, "000000"));
        Assert.Equal((HttpStatusCode.BadRequest, "invitation_invalid"), await Accept(invite + "x", WebHarness.Password, code));
        Assert.Equal((HttpStatusCode.BadRequest, "invitation_invalid"), await Accept("garbage", WebHarness.Password, code));
        Assert.Equal((HttpStatusCode.Created, null), await Accept(invite, WebHarness.Password, code));
        Assert.Equal((HttpStatusCode.BadRequest, "invitation_invalid"), await Accept(invite, WebHarness.Password, code)); // already used
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.PostAsJsonAsync("/api/invitations/accept", new { token = "t" })).StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdminOfTheOrganisationCanInviteAndRefusalsAreAudited()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgA, inviteA) = await web.OrgWithInvitationAsync("admin-a@example.test", orgName: "Org A");
        var (orgB, inviteB) = await web.OrgWithInvitationAsync("admin-b@example.test", orgName: "Org B");
        var inviteViewer = await web.Service<InvitationService>()
            .InviteAsync(TenantScope.ForTenant(orgA), "system", "viewer-a@example.test", ["viewer"]);

        var tokenA = await web.SignInAsync("admin-a@example.test", await web.AcceptAsync(inviteA));
        var tokenB = await web.SignInAsync("admin-b@example.test", await web.AcceptAsync(inviteB));
        var tokenViewer = await web.SignInAsync("viewer-a@example.test", await web.AcceptAsync(inviteViewer));
        var invite = new { email = "colleague@example.test", roleKeys = new[] { "analyst" } };

        var ok = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgA}/invitations", tokenA, invite));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(ok)).GetProperty("inviteToken").GetString()));

        var asViewer = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgA}/invitations", tokenViewer, invite));
        var crossTenant = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgA}/invitations", tokenB, invite));
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

        var badRole = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgA}/invitations", tokenA, new { email = "x@example.test", roleKeys = new[] { "superuser" } }));
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Equal("unknown_role", (await Json(badRole)).GetProperty("error").GetString());

        // Both refusals were recorded in Org A's audit trail; Org B's trail has nothing about them.
        var store = web.Service<KernelStore>();
        var denied = (await store.Audit.FindAsync(TenantScope.ForTenant(orgA), e => e.Action == "access.denied")).ToList();
        Assert.Equal(2, denied.Count);
        Assert.All(denied, e => Assert.Equal(AuditService.Denied, e.Outcome));
        Assert.Empty(await store.Audit.FindAsync(TenantScope.ForTenant(orgB), e => e.Action == "access.denied"));
    }

    [Fact]
    public async Task AnExistingUserJoinsASecondOrganisationAndSeesBothInTheirProfile()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgA, inviteA) = await web.OrgWithInvitationAsync("multi@example.test", orgName: "Org A");
        var (orgB, inviteB) = await web.OrgWithInvitationAsync("multi@example.test", "viewer", orgName: "Org B");

        var secret = await web.AcceptAsync(inviteA);
        var duplicate = await web.Http.PostAsJsonAsync("/api/invitations/accept",
            new { token = inviteB, password = WebHarness.Password, language = "en", totpSecret = secret, totpCode = Totp.CodeAt(secret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("account_exists", (await Json(duplicate)).GetProperty("error").GetString());

        var token = await web.SignInAsync("multi@example.test", secret);
        var join = await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/invitations/accept-existing", token, new { token = inviteB }));
        Assert.Equal(HttpStatusCode.NoContent, join.StatusCode);

        var me = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token)));
        var ids = me.GetProperty("organisations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { orgA, orgB }.Order(), ids.Order());
    }

    [Fact]
    public async Task AuthRoutesAreRateLimitedPerClient()
    {
        await using var web = await WebHarness.StartAsync(authPerMinute: 3);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = "a@b.test", password = "wrong password" })).StatusCode);

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheConfiguredFrontendOriginIsAllowedByCors()
    {
        await using var web = await WebHarness.StartAsync();

        async Task<string?> AllowedOriginFor(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/api/me");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "GET");
            req.Headers.Add("Access-Control-Request-Headers", "authorization");
            var res = await web.Http.SendAsync(req);
            return res.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
        }

        Assert.Equal(WebHarness.AllowedOrigin, await AllowedOriginFor(WebHarness.AllowedOrigin));
        Assert.Null(await AllowedOriginFor("https://evil.example"));
    }

    [Fact]
    public async Task TotpSecretEndpointReturnsAFreshSecretAndAnAuthenticatorUri()
    {
        await using var web = await WebHarness.StartAsync();

        var a = await Json(await web.Http.GetAsync("/api/auth/totp-secret?label=admin%40delta.example"));
        var b = await Json(await web.Http.GetAsync("/api/auth/totp-secret"));

        Assert.NotEqual(a.GetProperty("secret").GetString(), b.GetProperty("secret").GetString());
        Assert.StartsWith("otpauth://totp/Zr3i:admin%40delta.example?secret=", a.GetProperty("otpauthUri").GetString(), StringComparison.Ordinal);
    }
}
