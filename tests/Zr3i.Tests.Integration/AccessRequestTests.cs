using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AccessRequestTests(MongoFixture mongo)
{
    private static SubmitAccessRequest Bank(Action<Dictionary<string, string?>>? fields = null, string email = "Head@Nile-Bank.example ")
    {
        var f = new Dictionary<string, string?> { ["commercial_register_number"] = "CR-123456" };
        fields?.Invoke(f);
        return new("bank", "Nile Bank", "Mona Hassan", email, "Verify declared area before disbursing input finance.", "en", f);
    }

    private static async Task<string[]> Codes(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    [Fact]
    public async Task AValidRequestIsStoredPendingInThePlatformTenant()
    {
        var k = new KernelHarness(mongo);

        var r = await k.AccessRequests.SubmitAsync(Bank());

        Assert.Equal(("pending", "bank", "head@nile-bank.example", PlatformTenant.Id), (r.Status, r.AccountType, r.Email, r.TenantId));
        Assert.Equal("CR-123456", r.Fields["commercial_register_number"]);
        Assert.Equal([r.Id], (await k.AccessRequests.ListAsync("pending")).Select(x => x.Id));
        Assert.Single(await k.Store.Audit.FindAsync(PlatformTenant.Scope, e => e.Action == "access_request.created"));
    }

    [Fact]
    public async Task EachPresetDecidesWhichFieldsAreRequired()
    {
        var k = new KernelHarness(mongo);

        Assert.Equal(["field_required:commercial_register_number"], await Codes(() => k.AccessRequests.SubmitAsync(Bank(f => f.Remove("commercial_register_number")))));
        Assert.Equal(["field_required:commercial_register_number"], await Codes(() => k.AccessRequests.SubmitAsync(Bank(f => f["commercial_register_number"] = "  "))));
        Assert.Equal(["unknown_field:favourite_colour"], await Codes(() => k.AccessRequests.SubmitAsync(Bank(f => f["favourite_colour"] = "green"))));

        var farmer = new SubmitAccessRequest("farmer", "Abu Ali Farm", "Ali Mahmoud", "ali@farm.example", "Manage my consent.", "ar", new Dictionary<string, string?>());
        Assert.Equal(["field_required:mobile_number"], await Codes(() => k.AccessRequests.SubmitAsync(farmer)));
        var ok = await k.AccessRequests.SubmitAsync(farmer with { Fields = new Dictionary<string, string?> { ["mobile_number"] = "01012345678" } });
        Assert.Equal(("farmer", "ar"), (ok.AccountType, ok.Language));
    }

    [Fact]
    public async Task ZrStaffAccountsAreInvitationOnlyAndUnknownTypesAreRefused()
    {
        var k = new KernelHarness(mongo);

        Assert.Equal(["signup_not_allowed"], await Codes(() => k.AccessRequests.SubmitAsync(Bank() with { AccountType = "zr3i-admin" })));
        Assert.Equal(["unknown_account_type"], await Codes(() => k.AccessRequests.SubmitAsync(Bank() with { AccountType = "spaceship" })));
        Assert.Empty(await k.AccessRequests.ListAsync(null));
    }

    [Fact]
    public async Task TheBasicsAreChecked()
    {
        var k = new KernelHarness(mongo);

        var bad = Bank() with { OrganisationName = " ", ContactName = "", Purpose = "", Email = "not-an-email", Language = "fr" };

        Assert.Equal(["contact_name_required", "invalid_email", "invalid_language", "organisation_name_required", "purpose_required"], (await Codes(() => k.AccessRequests.SubmitAsync(bad))).Order());
        Assert.Equal(["invalid_email"], await Codes(() => k.AccessRequests.SubmitAsync(Bank(email: "has space@x.example"))));
        Assert.Equal(["unknown_inviter"], await Codes(() => k.AccessRequests.SubmitAsync(Bank() with { InvitedByOrganisationId = "no-such-org" })));
    }

    [Fact]
    public async Task AFarmerMayNameTheCompanyThatInvitedThem()
    {
        var k = new KernelHarness(mongo);
        var company = await k.Organisations.CreateAsync("AgriCash", "company");

        var r = await k.AccessRequests.SubmitAsync(new("farmer", "Abu Ali Farm", "Ali", "ali@farm.example", "Consent.", "ar",
            new Dictionary<string, string?> { ["mobile_number"] = "010" }, company.Id));

        Assert.Equal(company.Id, r.InvitedByOrganisationId);
    }

    [Fact]
    public async Task OnlyOnePendingRequestPerEmailAndTypeButAnotherAfterADecision()
    {
        var k = new KernelHarness(mongo);
        var first = await k.AccessRequests.SubmitAsync(Bank());

        Assert.Equal(["duplicate_pending"], await Codes(() => k.AccessRequests.SubmitAsync(Bank(email: "HEAD@nile-bank.example"))));
        await k.AccessRequests.RejectAsync("admin-1", first.Id, "not_a_customer");

        Assert.Equal("pending", (await k.AccessRequests.SubmitAsync(Bank())).Status);
    }

    [Fact]
    public async Task ApprovalCreatesTheOrganisationFromItsPresetAndInvitesItsFirstAdmin()
    {
        var k = new KernelHarness(mongo);
        var request = await k.AccessRequests.SubmitAsync(Bank());

        var (approved, token) = await k.AccessRequests.ApproveAsync("admin-1", request.Id);

        Assert.Equal(("approved", "admin-1"), (approved.Status, approved.DecidedBy));
        var org = (await k.Store.Organisations.GetAsync(TenantScope.ForTenant(approved.OrganisationId!), approved.OrganisationId!))!;
        Assert.Equal(("Nile Bank", "bank"), (org.Name, org.AccountType));
        Assert.Equal(["analyst", "org-admin", "viewer"], (await k.Store.Roles.FindAsync(TenantScope.ForTenant(org.Id), _ => true)).Select(r => r.Key).Order());

        var secret = Totp.NewSecret();
        var admin = await k.Invitations.AcceptAsync(token, KernelHarness.Password, "en", secret, Totp.CodeAt(secret, k.Clock.GetUtcNow()));
        Assert.Equal("head@nile-bank.example", admin.Email);
        Assert.True(await k.Authorization.IsAllowedAsync(admin.Id, org.Id, "kernel:users:manage"));
        Assert.Contains(await k.Store.Audit.FindAsync(PlatformTenant.Scope, _ => true), e => e.Action == "access_request.approved" && e.ActorUserId == "admin-1");
    }

    [Fact]
    public async Task ARejectedRequestCreatesNothingAndADecisionIsFinal()
    {
        var k = new KernelHarness(mongo);
        var request = await k.AccessRequests.SubmitAsync(Bank());

        var rejected = await k.AccessRequests.RejectAsync("admin-1", request.Id, "unverified_register_number");

        Assert.Equal(("rejected", "unverified_register_number", null), (rejected.Status, rejected.DecisionReason, rejected.OrganisationId));
        Assert.Equal(0, await k.Store.Organisations.CountAsync(PlatformTenant.Scope, o => o.Name == "Nile Bank"));
        Assert.Equal(["not_pending"], await Codes(() => k.AccessRequests.ApproveAsync("admin-1", request.Id)));
        Assert.Equal(["not_pending"], await Codes(() => k.AccessRequests.RejectAsync("admin-1", request.Id, "x")));
        await Assert.ThrowsAsync<NotFoundException>(() => k.AccessRequests.ApproveAsync("admin-1", "nope"));
    }

    [Fact]
    public async Task AnApprovedRequestCannotBeApprovedAgain()
    {
        var k = new KernelHarness(mongo);
        var request = await k.AccessRequests.SubmitAsync(Bank());
        var (first, _) = await k.AccessRequests.ApproveAsync("admin-1", request.Id);

        Assert.Equal(["not_pending"], await Codes(() => k.AccessRequests.ApproveAsync("admin-1", request.Id)));

        var approved = await k.AccessRequests.ListAsync("approved");
        Assert.Equal(first.OrganisationId, approved.Single().OrganisationId);       // still the one organisation made the first time
    }
}
