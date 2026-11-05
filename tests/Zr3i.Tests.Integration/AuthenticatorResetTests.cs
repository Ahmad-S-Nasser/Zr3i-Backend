using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>An organisation admin resets a member's authenticator; the member cannot sign in until they re-enrol with a one-time link.</summary>
[Collection(MongoCollection.Name)]
public class AuthenticatorResetTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Person(string OrgId, string Email, string UserId, string Secret, string Token);

    private static async Task<Person> JoinAsync(WebHarness web, string orgId, string email, string role)
    {
        var invite = await web.Service<InvitationService>().InviteAsync(TenantScope.ForTenant(orgId), "system", email, [role]);
        var secret = await web.AcceptAsync(invite);
        var token = await web.SignInAsync(email, secret);
        var me = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token)));
        return new Person(orgId, email, me.GetProperty("id").GetString()!, secret, token);
    }

    private static Task<HttpResponseMessage> Reset(WebHarness web, string orgId, string userId, string token) =>
        web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgId}/members/{userId}/reset-authenticator", token));

    private static Task<HttpResponseMessage> Reenrol(WebHarness web, string link, string password, string secret, string? code = null) =>
        web.Http.PostAsJsonAsync("/api/authenticator-reset", new { token = link, password, totpSecret = secret, totpCode = code ?? Totp.CodeAt(secret, DateTimeOffset.UtcNow) });

    private static async Task<bool> CanSignInAsync(WebHarness web, string email, string secret)
    {
        var first = await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email, password = WebHarness.Password });
        if (!first.IsSuccessStatusCode) return false;
        var challenge = (await Json(first)).GetProperty("challenge").GetString();
        var second = await web.Http.PostAsJsonAsync("/api/auth/verify", new { challenge, code = Totp.CodeAt(secret, DateTimeOffset.UtcNow) });
        return second.IsSuccessStatusCode;
    }

    private static async Task<(string OrgId, Person Admin, Person Member)> SeedAsync(WebHarness web)
    {
        var (orgId, adminInvite) = await web.OrgWithInvitationAsync("admin@delta.example", orgName: "Delta Agro");
        var adminSecret = await web.AcceptAsync(adminInvite);
        var adminToken = await web.SignInAsync("admin@delta.example", adminSecret);
        var me = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", adminToken)));
        var admin = new Person(orgId, "admin@delta.example", me.GetProperty("id").GetString()!, adminSecret, adminToken);
        return (orgId, admin, await JoinAsync(web, orgId, "analyst@delta.example", "analyst"));
    }

    [Fact]
    public async Task ResettingEndsTheMembersSessionAndBlocksSignInEvenWithTheRightPasswordAndCode()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", member.Token))).StatusCode);

        var response = await Reset(web, orgId, member.UserId, admin.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(response)).GetProperty("token").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", member.Token))).StatusCode);
        Assert.False(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task TheMemberReEnrolsWithTheLinkTheirPasswordAndANewSecretThenSignsInWithItAlone()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var link = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;
        var fresh = Totp.NewSecret();

        Assert.Equal(HttpStatusCode.NoContent, (await Reenrol(web, link, WebHarness.Password, fresh)).StatusCode);

        Assert.True(await CanSignInAsync(web, member.Email, fresh));
        Assert.False(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task TheLinkWorksOnceAndAWrongPasswordOrCodeDoesNotUseItUp()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var link = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;
        var fresh = Totp.NewSecret();

        Assert.Equal(HttpStatusCode.BadRequest, (await Reenrol(web, link, "not the password at all", fresh)).StatusCode);
        var badCode = await Reenrol(web, link, WebHarness.Password, fresh, "000000");
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Equal("bad_code", (await Json(badCode)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Reenrol(web, link, WebHarness.Password, fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reenrol(web, link, WebHarness.Password, Totp.NewSecret())).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0123456789abcdef01234567.wrongsecret")]
    public async Task AnInvalidLinkIsRefusedWithTheSameAnswer(string link)
    {
        await using var web = await WebHarness.StartAsync();
        var (_, _, member) = await SeedAsync(web);

        var response = await Reenrol(web, link, WebHarness.Password, Totp.NewSecret());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_reset", (await Json(response)).GetProperty("error").GetString());
        Assert.True(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task TheLinkForAnotherPersonDoesNotReEnrolThisOne()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var other = await JoinAsync(web, orgId, "viewer@delta.example", "viewer");
        var linkForMember = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;

        var forged = other.UserId + linkForMember[linkForMember.IndexOf('.')..];
        Assert.Equal(HttpStatusCode.BadRequest, (await Reenrol(web, forged, WebHarness.Password, Totp.NewSecret())).StatusCode);
        Assert.True(await CanSignInAsync(web, other.Email, other.Secret));
    }

    [Fact]
    public async Task OnlyAnAdminCanReset()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var viewer = await JoinAsync(web, orgId, "viewer@delta.example", "viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await Reset(web, orgId, admin.UserId, member.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reset(web, orgId, member.UserId, viewer.Token)).StatusCode);
        Assert.True(await CanSignInAsync(web, admin.Email, admin.Secret));
    }

    [Fact]
    public async Task AnotherOrganisationsAdminCannotResetThisOrganisationsMember()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, _, member) = await SeedAsync(web);
        var (otherOrg, otherInvite) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other Co");
        var otherAdmin = await web.SignInAsync("admin@other.example", await web.AcceptAsync(otherInvite));

        Assert.Equal(HttpStatusCode.Forbidden, (await Reset(web, orgId, member.UserId, otherAdmin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reset(web, otherOrg, member.UserId, otherAdmin)).StatusCode);
        Assert.True(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task AnAdminCannotResetThemselvesOrSomeoneWhoAlsoBelongsToAnotherOrganisation()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);

        var self = await Reset(web, orgId, admin.UserId, admin.Token);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var (otherOrg, _) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other Co");
        var token = await web.Service<InvitationService>().InviteAsync(TenantScope.ForTenant(otherOrg), "system", member.Email, ["viewer"]);
        var memberSession = await web.SignInAsync(member.Email, member.Secret);
        (await web.Http.SendAsync(web.Request(HttpMethod.Post, "/api/invitations/accept-existing", memberSession, new { token }))).EnsureSuccessStatusCode();

        var shared = await Reset(web, orgId, member.UserId, admin.Token);
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode);
        Assert.True(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task AnInactiveMemberCannotBeReset()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var scope = TenantScope.ForTenant(orgId);
        var store = web.Service<KernelStore>();
        var membership = (await store.Memberships.FindAsync(scope, m => m.UserId == member.UserId)).Single();
        membership.IsActive = false;
        await store.Memberships.ReplaceAsync(scope, membership);

        Assert.Equal(HttpStatusCode.NotFound, (await Reset(web, orgId, member.UserId, admin.Token)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredLinkIsRefused()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var link = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;
        var user = (await web.Service<KernelStore>().Users.GetAsync(PlatformTenant.Scope, member.UserId))!;
        user.ResetExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await web.Service<KernelStore>().Users.ReplaceAsync(PlatformTenant.Scope, user);

        Assert.Equal(HttpStatusCode.BadRequest, (await Reenrol(web, link, WebHarness.Password, Totp.NewSecret())).StatusCode);
        Assert.False(await CanSignInAsync(web, member.Email, member.Secret));
    }

    [Fact]
    public async Task TheMemberListShowsThatAReEnrolmentIsPendingAndBothStepsAreAudited()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var link = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;

        async Task<bool> Enrolled()
        {
            var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/members", admin.Token)));
            return list.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetString() == member.UserId).GetProperty("authenticatorEnrolled").GetBoolean();
        }

        Assert.False(await Enrolled());
        await Reenrol(web, link, WebHarness.Password, Totp.NewSecret());
        Assert.True(await Enrolled());

        var actions = (await web.Service<KernelStore>().Audit.FindAsync(TenantScope.ForTenant(orgId), e => e.ObjectId == member.UserId)).Select(e => e.Action).ToList();
        Assert.Contains("authenticator.reset_requested", actions);
        Assert.Contains("authenticator.reset_completed", actions);
    }

    [Fact]
    public async Task NeitherTheLinkNorTheNewSecretIsStoredOrShownInTheMemberList()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var link = (await Json(await Reset(web, orgId, member.UserId, admin.Token))).GetProperty("token").GetString()!;
        var secretPart = link[(link.IndexOf('.') + 1)..];

        var user = (await web.Service<KernelStore>().Users.GetAsync(PlatformTenant.Scope, member.UserId))!;
        Assert.NotEqual(secretPart, user.ResetTokenHash);
        Assert.DoesNotContain(secretPart, JsonSerializer.Serialize(user));
        var members = await (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/members", admin.Token))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretPart, members);
        Assert.DoesNotContain("resetTokenHash", members, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ThePasswordStepAloneIsRefusedWhileAReEnrolmentIsPending()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        await Reset(web, orgId, member.UserId, admin.Token);

        var first = await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = member.Email, password = WebHarness.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
    }

    [Fact]
    public async Task ASignInStartedBeforeTheResetCannotBeFinishedAfterIt()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        var first = await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = member.Email, password = WebHarness.Password });
        var challenge = (await Json(first)).GetProperty("challenge").GetString();
        await Reset(web, orgId, member.UserId, admin.Token);

        var second = await web.Http.PostAsJsonAsync("/api/auth/verify", new { challenge, code = Totp.CodeAt(member.Secret, DateTimeOffset.UtcNow) });

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task APendingMembersLinkWithTheWrongSecretIsRefused()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin, member) = await SeedAsync(web);
        await Reset(web, orgId, member.UserId, admin.Token);

        var response = await Reenrol(web, member.UserId + ".not-the-real-secret", WebHarness.Password, Totp.NewSecret());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await CanSignInAsync(web, member.Email, member.Secret));
    }
}
