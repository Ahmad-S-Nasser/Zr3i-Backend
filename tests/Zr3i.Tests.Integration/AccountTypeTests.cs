using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AccountTypeTests(MongoFixture mongo)
{
    private KernelHarness New() => new(mongo);

    [Fact]
    public async Task TheFiveBuiltInTypesAreCreatedOnFirstUseFromDataNotCode()
    {
        var k = New();

        var all = await k.AccountTypes.ListAsync();

        Assert.Equal(["bank", "company", "farmer", "insurance", "zr3i-admin"], all.Select(t => t.Key).Order());
        var company = all.Single(t => t.Key == "company");
        Assert.Equal("portfolio", company.HomeView);
        Assert.Equal(["portfolio", "farms", "parcels", "farmers", "risk", "analysis", "accuracy", "imports", "methodology", "users", "audit"], company.Navigation);
        Assert.Equal(["my-farms", "consent", "reports"], all.Single(t => t.Key == "farmer").Navigation);
        Assert.Equal(PlatformTenant.Id, company.TenantId);
        Assert.All(all, t => { Assert.NotEmpty(t.LabelEn); Assert.NotEmpty(t.LabelAr); Assert.Contains("org-admin", t.RoleKeys); });
    }

    [Fact]
    public async Task OnlyZr3iStaffAccountsAreInvitationOnly()
    {
        var all = await New().AccountTypes.ListAsync();

        Assert.Equal(["zr3i-admin"], all.Where(t => !t.SelfSignup).Select(t => t.Key));
        Assert.Equal(["commercial_register_number"], all.Single(t => t.Key == "bank").SignupFields.Select(f => f.Key));
        Assert.Equal(["mobile_number"], all.Single(t => t.Key == "farmer").SignupFields.Select(f => f.Key));
    }

    [Fact]
    public async Task AnEditedPresetIsNeverOverwrittenByTheBuiltIns()
    {
        var k = New();
        var bank = (await k.AccountTypes.ListAsync()).Single(t => t.Key == "bank");
        bank.Navigation = ["tranches", "reports", "my-extra-screen"];
        await k.Store.AccountTypes.ReplaceAsync(PlatformTenant.Scope, bank);

        var restarted = new AccountTypeService(k.Store);          // a fresh process: nothing cached
        await restarted.EnsureBuiltInsAsync();

        Assert.Contains("my-extra-screen", (await restarted.GetAsync("bank"))!.Navigation);
        Assert.Equal(5, (await restarted.ListAsync()).Count);
    }

    [Theory]
    [InlineData("bank", new[] { "analyst", "org-admin", "viewer" }, new[] { "verification", "risk" })]
    [InlineData("company", new[] { "analyst", "field-user", "org-admin", "viewer" }, new[] { "verification", "risk" })]
    [InlineData("farmer", new[] { "org-admin", "viewer" }, new[] { "verification" })]
    [InlineData("zr3i-admin", new[] { "analyst", "org-admin" }, new[] { "verification", "risk" })]
    public async Task AnOrganisationGetsTheRolesAndModulesOfItsPreset(string type, string[] roles, string[] modules)
    {
        var k = New();

        var org = await k.Organisations.CreateAsync("Some organisation", type);

        Assert.Equal(type, org.AccountType);
        Assert.Equal(modules, org.Modules);
        Assert.Equal(roles, (await k.Store.Roles.FindAsync(TenantScope.ForTenant(org.Id), _ => true)).Select(r => r.Key).Order());
    }

    [Fact]
    public async Task AnUnknownTypeIsRefused()
    {
        var k = New();

        var e = await Assert.ThrowsAsync<ValidationFailedException>(() => k.Organisations.CreateAsync("X", "spaceship"));

        Assert.Equal(["unknown_account_type"], e.Codes);
    }

    [Fact]
    public async Task TheProviderOrganisationIsBootstrappedOnceWithOneInvitationForItsFirstAdmin()
    {
        var k = New();

        var token = await k.Platform.BootstrapAsync("founder@zr3i.example");
        Assert.NotNull(token);
        Assert.Null(await k.Platform.BootstrapAsync("founder@zr3i.example"));          // invitation still pending
        Assert.Equal(1, await k.Store.Organisations.CountAsync(PlatformTenant.Scope, _ => true));

        var provider = (await k.Store.Organisations.FindAsync(PlatformTenant.Scope, _ => true)).Single();
        Assert.Equal((PlatformTenant.Id, PlatformTenant.Id, "zr3i-admin"), (provider.Id, provider.TenantId, provider.AccountType));

        var secret = Totp.NewSecret();
        var admin = await k.Invitations.AcceptAsync(token, KernelHarness.Password, "en", secret, Totp.CodeAt(secret, k.Clock.GetUtcNow()));
        Assert.Null(await k.Platform.BootstrapAsync("founder@zr3i.example"));          // has a member now
        Assert.True(await k.Authorization.IsAllowedAsync(admin.Id, PlatformTenant.Id, "platform:access-requests:manage"));
    }

    [Fact]
    public async Task AnOrdinaryOrganisationAdminHasNoPlatformPowers()
    {
        var k = New();
        var (org, user, _) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");

        Assert.True(await k.Authorization.IsAllowedAsync(user.Id, org.Id, "kernel:users:manage"));
        Assert.False(await k.Authorization.IsAllowedAsync(user.Id, PlatformTenant.Id, "platform:access-requests:manage"));
    }
}
