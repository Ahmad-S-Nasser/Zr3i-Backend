using Zr3i.Kernel;
using Zr3i.Data;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class IdentityFlowTests(MongoFixture mongo)
{
    private KernelHarness NewKernel() => new(mongo);

    [Fact]
    public async Task NewOrganisationGetsTheFourRoleTemplates()
    {
        var k = NewKernel();
        var org = await k.Organisations.CreateAsync("Delta Agro", "company");

        Assert.Equal(org.Id, org.TenantId);
        var roles = await k.Store.Roles.FindAsync(TenantScope.ForTenant(org.Id), _ => true);
        Assert.Equal(["analyst", "field-user", "org-admin", "viewer"], roles.Select(r => r.Key).Order());
    }

    [Fact]
    public async Task InvitedUserAcceptsSignsInInTwoStepsAndGetsTheirPermissions()
    {
        var k = NewKernel();
        var (org, user, secret) = await k.OrgWithAdminAsync("Delta Agro", "Admin@Delta.example ");

        Assert.Equal("admin@delta.example", user.Email);
        Assert.NotEqual(KernelHarness.Password, user.PasswordHash);

        var session = await k.FullSignInAsync("admin@delta.example", secret);
        Assert.NotNull(session);
        var info = await k.Sessions.ValidateAsync(session);
        Assert.Equal(user.Id, info!.UserId);

        Assert.True(await k.Authorization.IsAllowedAsync(user.Id, org.Id, "kernel:users:manage"));
        Assert.False(await k.Authorization.IsAllowedAsync(user.Id, "some-other-tenant", "kernel:users:manage"));
    }

    [Fact]
    public async Task SignInFailuresAreGenericAndUnknownEmailLooksTheSame()
    {
        var k = NewKernel();
        var (_, _, _) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", "wrong password here"));
        Assert.Null(await k.SignIn.BeginAsync("nobody@delta.example", KernelHarness.Password));
    }

    [Fact]
    public async Task FiveFailedPasswordsLockTheAccountForFifteenMinutes()
    {
        var k = NewKernel();
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        for (var i = 0; i < 5; i++) Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", "wrong password " + i));

        // Even the right password is refused while locked.
        Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", KernelHarness.Password));
        k.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", KernelHarness.Password));
        k.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await k.FullSignInAsync("admin@delta.example", secret));
    }

    [Fact]
    public async Task AGoodSignInResetsTheFailureCount()
    {
        var k = NewKernel();
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        for (var i = 0; i < 4; i++) Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", "wrong password " + i));
        Assert.NotNull(await k.FullSignInAsync("admin@delta.example", secret));
        for (var i = 0; i < 4; i++) Assert.Null(await k.SignIn.BeginAsync("admin@delta.example", "wrong again " + i));
        Assert.NotNull(await k.FullSignInAsync("admin@delta.example", secret));
    }

    [Fact]
    public async Task WrongOneTimeCodeIsRefusedAndAChallengeWorksOnce()
    {
        var k = NewKernel();
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        var challenge = await k.SignIn.BeginAsync("admin@delta.example", KernelHarness.Password);
        Assert.Null(await k.SignIn.CompleteAsync(challenge!, "000000"));

        var good = Totp.CodeAt(secret, k.Clock.GetUtcNow());
        Assert.NotNull(await k.SignIn.CompleteAsync(challenge!, good));
        Assert.Null(await k.SignIn.CompleteAsync(challenge!, good));
    }

    [Fact]
    public async Task AChallengeExpiresAfterFiveMinutes()
    {
        var k = NewKernel();
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        var challenge = await k.SignIn.BeginAsync("admin@delta.example", KernelHarness.Password);
        k.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await k.SignIn.CompleteAsync(challenge!, Totp.CodeAt(secret, k.Clock.GetUtcNow())));
    }

    [Fact]
    public async Task SessionsEndAfterTwelveHoursOrWhenRevoked()
    {
        var k = NewKernel();
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        var s1 = (await k.FullSignInAsync("admin@delta.example", secret))!;
        var s2 = (await k.FullSignInAsync("admin@delta.example", secret))!;
        Assert.NotNull(await k.Sessions.ValidateAsync(s1));

        await k.Sessions.RevokeAsync(s1);
        Assert.Null(await k.Sessions.ValidateAsync(s1));

        k.Clock.Advance(TimeSpan.FromHours(12).Add(TimeSpan.FromSeconds(1)));
        Assert.Null(await k.Sessions.ValidateAsync(s2));
        Assert.Null(await k.Sessions.ValidateAsync("garbage"));
        Assert.Null(await k.Sessions.ValidateAsync(s2 + "x"));
    }

    [Fact]
    public async Task InvitationRefusesWeakPasswordsBadCodesReuseAndExpiry()
    {
        var k = NewKernel();
        var org = await k.Organisations.CreateAsync("Delta Agro", "company");
        var scope = TenantScope.ForTenant(org.Id);
        var secret = Totp.NewSecret();
        var code = Totp.CodeAt(secret, k.Clock.GetUtcNow());

        var token = await k.Invitations.InviteAsync(scope, "system", "new@delta.example", ["viewer"]);
        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsync(token, "short", "en", secret, code));
        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsync(token, KernelHarness.Password, "en", secret, "000000"));
        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsync(token + "x", KernelHarness.Password, "en", secret, code));
        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsync("no-dot-token", KernelHarness.Password, "en", secret, code));

        await k.Invitations.AcceptAsync(token, KernelHarness.Password, "ar", secret, code);
        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsync(token, KernelHarness.Password, "en", secret, code));

        var late = await k.Invitations.InviteAsync(scope, "system", "late@delta.example", ["viewer"]);
        k.Clock.Advance(TimeSpan.FromDays(8));
        var lateSecret = Totp.NewSecret();
        await Assert.ThrowsAsync<InvitationException>(() =>
            k.Invitations.AcceptAsync(late, KernelHarness.Password, "en", lateSecret, Totp.CodeAt(lateSecret, k.Clock.GetUtcNow())));
    }

    [Fact]
    public async Task InvitingAnUnknownRoleFails()
    {
        var k = NewKernel();
        var org = await k.Organisations.CreateAsync("Delta Agro", "company");

        await Assert.ThrowsAsync<InvitationException>(() =>
            k.Invitations.InviteAsync(TenantScope.ForTenant(org.Id), "system", "x@delta.example", ["superuser"]));
    }

    [Fact]
    public async Task OneUserCanJoinASecondOrganisationAndKeepsSeparateAccess()
    {
        var k = NewKernel();
        var (orgA, user, secret) = await k.OrgWithAdminAsync("Delta Agro", "multi@example.test");
        var orgB = await k.Organisations.CreateAsync("Nile Bank", "bank");

        var token = await k.Invitations.InviteAsync(TenantScope.ForTenant(orgB.Id), "system", "multi@example.test", ["viewer"]);
        await k.Invitations.AcceptAsExistingUserAsync(token, user.Id);

        Assert.True(await k.Authorization.IsAllowedAsync(user.Id, orgA.Id, "kernel:users:manage"));
        Assert.True(await k.Authorization.IsAllowedAsync(user.Id, orgB.Id, "risk:fri:read"));
        Assert.False(await k.Authorization.IsAllowedAsync(user.Id, orgB.Id, "kernel:users:manage"));
        Assert.NotNull(await k.FullSignInAsync("multi@example.test", secret));
    }

    [Fact]
    public async Task AcceptingAsExistingUserNeedsTheMatchingEmail()
    {
        var k = NewKernel();
        var (_, other, _) = await k.OrgWithAdminAsync("Delta Agro", "someone.else@example.test");
        var orgB = await k.Organisations.CreateAsync("Nile Bank", "bank");

        var token = await k.Invitations.InviteAsync(TenantScope.ForTenant(orgB.Id), "system", "invited@example.test", ["viewer"]);

        await Assert.ThrowsAsync<InvitationException>(() => k.Invitations.AcceptAsExistingUserAsync(token, other.Id));
    }

    [Fact]
    public async Task DeactivatedMembersAndUsersLoseAccess()
    {
        var k = NewKernel();
        var (org, user, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        var members = await k.Store.Memberships.FindAsync(TenantScope.ForTenant(org.Id), m => m.UserId == user.Id);
        members[0].IsActive = false;
        await k.Store.Memberships.ReplaceAsync(TenantScope.ForTenant(org.Id), members[0]);
        Assert.False(await k.Authorization.IsAllowedAsync(user.Id, org.Id, "kernel:users:manage"));

        user.IsActive = false;
        await k.Store.Users.ReplaceAsync(PlatformTenant.Scope, user);
        Assert.Null(await k.FullSignInAsync("admin@delta.example", secret));
    }
}
