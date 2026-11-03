using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ConsentTests(MongoFixture mongo)
{
    /// <summary>
    /// AgriCash (a company, holds the records) manages farmer Ali's farm with three parcels, and farmer Omar's farm with one.
    /// Nile Bank and Delta Insurance are other organisations that may be given access.
    /// </summary>
    private sealed record World(
        KernelHarness K, Organisation Company, Organisation Bank, Organisation Insurer,
        Farmer Ali, Farmer Omar, Parcel[] AliParcels, Parcel OmarParcel)
    {
        public TenantScope A => TenantScope.ForTenant(Company.Id);
        public TenantScope B => TenantScope.ForTenant(Bank.Id);
        public TenantScope C => TenantScope.ForTenant(Insurer.Id);
        public GrantTerms ToBank(int days = 90, IReadOnlyList<string>? parcels = null, DateTime? from = null) =>
            new(Ali.Id, Bank.Id, "Verify input finance", K.Clock.GetUtcNow().UtcDateTime.AddDays(days), parcels, from);
    }

    private async Task<World> WorldAsync(string? aliAccountOrg = null)
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        var company = await k.Organisations.CreateAsync("AgriCash", "company");
        var bank = await k.Organisations.CreateAsync("Nile Bank", "bank");
        var insurer = await k.Organisations.CreateAsync("Delta Insurance", "insurance");
        var a = TenantScope.ForTenant(company.Id);

        var ali = await k.FarmerService.CreateAsync(a, "u1", new NewFarmer("Ali Mahmoud", "01012345678", "ar", AccountOrganisationId: aliAccountOrg));
        var omar = await k.FarmerService.CreateAsync(a, "u1", new NewFarmer("Omar Said"));
        var aliFarm = await k.Farms.CreateFarmAsync(a, "u1", new NewFarm("Ali's farm", FarmerId: ali.Id));
        var omarFarm = await k.Farms.CreateFarmAsync(a, "u1", new NewFarm("Omar's farm", FarmerId: omar.Id));
        var aliParcels = new List<Parcel>();
        for (var i = 0; i < 3; i++) aliParcels.Add(await k.Farms.CreateParcelAsync(a, "u1", new NewParcel(aliFarm.Id, $"Ali {i}")));
        var omarParcel = await k.Farms.CreateParcelAsync(a, "u1", new NewParcel(omarFarm.Id, "Omar 0"));
        return new World(k, company, bank, insurer, ali, omar, [.. aliParcels], omarParcel);
    }

    private static async Task<(string GrantId, string WithdrawToken)> LinkGrantAsync(World w, GrantTerms terms, string language = "ar")
    {
        var (_, token) = await w.K.Consent.RequestAsync(w.A, "analyst-1", terms);
        var response = await w.K.Consent.RespondAsync(token, accepted: true, language);
        return (response.GrantId!, response.WithdrawToken!);
    }

    private static async Task<string[]> Refused(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    // ---- the farmer's one-page consent ----

    [Fact]
    public async Task AFarmerAgreesThroughTheLinkAndTheBankCanThenSeeTheGrantAndReadThroughTheGate()
    {
        var w = await WorldAsync();
        var (request, token) = await w.K.Consent.RequestAsync(w.A, "analyst-1", w.ToBank());

        var offer = await w.K.Consent.DescribeAsync(token);
        Assert.Equal(("AgriCash", "Nile Bank", "Ali Mahmoud", "ar", "Verify input finance", "pending"),
            (offer.RequesterName, offer.GranteeName, offer.FarmerName, offer.Language, offer.Purpose, offer.Status));
        Assert.Null(offer.ParcelCount);
        Assert.Equal(ConsentDataCategories.All, offer.DataCategories);
        Assert.Empty(await w.K.Consent.ListAsync(w.B));      // nothing is shared until the farmer agrees

        var response = await w.K.Consent.RespondAsync(token, accepted: true, "ar");
        Assert.True(response.Granted);
        Assert.NotNull(response.WithdrawToken);

        var seenByBank = (await w.K.Consent.ListAsync(w.B)).Single();
        Assert.Equal((response.GrantId, "grantee", "active", "link"), (seenByBank.GrantId, seenByBank.Role, seenByBank.Status, seenByBank.Channel));
        Assert.Equal("owner", (await w.K.Consent.ListAsync(w.A)).Single().Role);

        var access = await w.K.Gate.OpenAsync(w.B, "bank-analyst-1", response.GrantId!);
        Assert.Equal(w.AliParcels.Select(p => p.Id).Order(), access.AllowedParcelIds.Order());   // Ali's three; never Omar's
        Assert.DoesNotContain(w.OmarParcel.Id, access.AllowedParcelIds);
        Assert.Equal(("AgriCash", "Verify input finance"), (access.GrantorName, access.Purpose));
        Assert.Equal(request.ValidUntil, access.ValidUntil);
    }

    [Fact]
    public async Task EveryReadThroughTheGateIsAuditedInBothOrganisations()
    {
        var w = await WorldAsync();
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank());

        await w.K.Gate.OpenAsync(w.B, "bank-analyst-1", grantId);
        await w.K.Gate.OpenAsync(w.B, "bank-analyst-2", grantId);

        foreach (var tenant in new[] { w.A, w.B })
        {
            var reads = await w.K.Store.Audit.FindAsync(tenant, e => e.Action == "consent.read");
            Assert.Equal(["bank-analyst-1", "bank-analyst-2"], reads.Select(e => e.ActorUserId).Order());
            Assert.All(reads, e => Assert.Equal(("Verify input finance", grantId), (e.Purpose, e.ObjectId)));
        }
    }

    [Fact]
    public async Task NothingIsSharedUnlessTheFarmerExplicitlyAgrees()
    {
        var w = await WorldAsync();
        var (request, token) = await w.K.Consent.RequestAsync(w.A, "analyst-1", w.ToBank());

        var declined = await w.K.Consent.RespondAsync(token, accepted: false, "en");

        Assert.False(declined.Granted);
        Assert.Empty(await w.K.Consent.ListAsync(w.A));
        Assert.Empty(await w.K.Consent.ListAsync(w.B));
        Assert.Equal("declined", (await w.K.Store.ConsentRequests.GetAsync(w.A, request.Id))!.Status);
        Assert.Equal(["already_answered"], await Refused(() => w.K.Consent.RespondAsync(token, accepted: true, "en")));      // a declined link cannot be revived
    }

    [Fact]
    public async Task ALinkWorksOnceAndOnlyBeforeItExpires()
    {
        var w = await WorldAsync();
        var (_, once) = await w.K.Consent.RequestAsync(w.A, "u", w.ToBank());
        await w.K.Consent.RespondAsync(once, true, "en");
        Assert.Equal(["already_answered"], await Refused(() => w.K.Consent.RespondAsync(once, true, "en")));
        Assert.Single(await w.K.Consent.ListAsync(w.B));

        var (_, late) = await w.K.Consent.RequestAsync(w.A, "u", w.ToBank());
        w.K.Clock.Advance(ConsentService.RequestLifetime + TimeSpan.FromMinutes(1));
        Assert.Equal(["request_expired"], await Refused(() => w.K.Consent.RespondAsync(late, true, "en")));
        Assert.Single(await w.K.Consent.ListAsync(w.B));
    }

    [Fact]
    public async Task ForgedBrokenAndForeignLinksAreRefused()
    {
        var w = await WorldAsync();
        var (_, token) = await w.K.Consent.RequestAsync(w.A, "u", w.ToBank());

        foreach (var bad in new[] { token + "x", "garbage", "", token[..^3] + "abc", "a.b.c" })
            Assert.Equal(["invalid_token"], await Refused(() => w.K.Consent.DescribeAsync(bad)));
        Assert.Equal(["invalid_language"], await Refused(() => w.K.Consent.RespondAsync(token, true, "fr")));
        Assert.Empty(await w.K.Consent.ListAsync(w.B));
    }

    // ---- withdrawal ----

    [Fact]
    public async Task WithdrawingWithTheLinkStopsTheNextReadAndEveryPartySeesIt()
    {
        var w = await WorldAsync();
        var (grantId, withdrawToken) = await LinkGrantAsync(w, w.ToBank());
        await w.K.Gate.OpenAsync(w.B, "bank-analyst-1", grantId);

        var withdrawn = await w.K.Consent.WithdrawByTokenAsync(withdrawToken);

        Assert.Equal("withdrawn", withdrawn.Status);
        var denied = await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "bank-analyst-1", grantId));
        Assert.Equal("withdrawn", denied.Reason);
        Assert.Equal("withdrawn", (await w.K.Consent.ListAsync(w.B)).Single().Status);
        Assert.Equal("withdrawn", (await w.K.Consent.ListAsync(w.A)).Single().Status);
        foreach (var tenant in new[] { w.A, w.B })
            Assert.Single(await w.K.Store.Audit.FindAsync(tenant, e => e.Action == "consent.refused" && e.Purpose == "withdrawn"));
    }

    [Fact]
    public async Task WithdrawingTwiceIsHarmlessAndAWrongTokenDoesNothing()
    {
        var w = await WorldAsync();
        var (grantId, withdrawToken) = await LinkGrantAsync(w, w.ToBank());

        await w.K.Consent.WithdrawByTokenAsync(withdrawToken);
        await w.K.Consent.WithdrawByTokenAsync(withdrawToken);

        Assert.Single(await w.K.Store.ConsentEvents.FindAsync(w.A, e => e.GrantId == grantId && e.Event == "withdrawn"));
        var (otherGrant, _) = await LinkGrantAsync(w, w.ToBank());
        Assert.Equal(["invalid_token"], await Refused(() => w.K.Consent.WithdrawByTokenAsync(withdrawToken[..^2] + "zz")));
        Assert.Equal(["invalid_token"], await Refused(() => w.K.Consent.WithdrawByTokenAsync($"{w.Company.Id}.{otherGrant}.{withdrawToken.Split('.')[^1]}")));   // another grant's id with this grant's secret
        Assert.Equal("active", (await w.K.Consent.ListAsync(w.B)).Single(g => g.GrantId == otherGrant).Status);
    }

    // ---- the period ----

    [Fact]
    public async Task AGrantIsOnlyValidInsideItsPeriod()
    {
        var w = await WorldAsync();
        var later = w.K.Clock.GetUtcNow().UtcDateTime.AddDays(10);
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank(days: 40, from: later));

        Assert.Equal("not_yet_valid", (await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "u", grantId))).Reason);
        Assert.Equal("scheduled", (await w.K.Consent.ListAsync(w.B)).Single().Status);

        w.K.Clock.Advance(TimeSpan.FromDays(11));
        Assert.NotNull(await w.K.Gate.OpenAsync(w.B, "u", grantId));

        w.K.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal("expired", (await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "u", grantId))).Reason);
        Assert.Equal("expired", (await w.K.Consent.ListAsync(w.B)).Single().Status);
    }

    // ---- scope ----

    [Fact]
    public async Task AGrantCanCoverOnlySomeParcelsAndTheRestAreOnlyCountedAsWithheld()
    {
        var w = await WorldAsync();
        var shared = new[] { w.AliParcels[0].Id, w.AliParcels[1].Id };
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank(parcels: shared));

        var access = await w.K.Gate.OpenAsync(w.B, "u", grantId);
        Assert.Equal(shared.Order(), access.AllowedParcelIds.Order());

        // The bank asks about three of Ali's parcels and one of Omar's: it gets two, and a count of two it cannot see.
        var (allowed, withheld) = access.Resolve([w.AliParcels[0].Id, w.AliParcels[2].Id, w.OmarParcel.Id, w.AliParcels[1].Id]);
        Assert.Equal(shared.Order(), allowed.Order());
        Assert.Equal(2, withheld);
        Assert.DoesNotContain(w.AliParcels[2].Id, allowed);
        Assert.DoesNotContain(w.OmarParcel.Id, allowed);
    }

    [Fact]
    public async Task NewParcelsOfTheFarmerAreCoveredByAnAllParcelsGrantButOtherFarmersAreNever()
    {
        var w = await WorldAsync();
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank());
        var farm = (await w.K.Store.Farms.FindAsync(w.A, f => f.FarmerId == w.Ali.Id)).Single();
        var added = await w.K.Farms.CreateParcelAsync(w.A, "u1", new NewParcel(farm.Id, "Ali 3"));

        var access = await w.K.Gate.OpenAsync(w.B, "u", grantId);

        Assert.Equal(4, access.AllowedParcelIds.Count);
        Assert.Contains(added.Id, access.AllowedParcelIds);
        Assert.DoesNotContain(w.OmarParcel.Id, access.AllowedParcelIds);
    }

    // ---- who may use a grant ----

    [Fact]
    public async Task OnlyTheNamedGranteeCanUseAGrant()
    {
        var w = await WorldAsync();
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank());

        var byInsurer = await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.C, "insurer-analyst", grantId));
        var byOwner = await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.A, "company-analyst", grantId));   // the owner has no "grantee" standing
        var unknown = await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "bank-analyst", "no-such-grant"));

        Assert.All(new[] { byInsurer, byOwner, unknown }, e => Assert.Equal("no_such_grant", e.Reason));
        Assert.Single(await w.K.Store.Audit.FindAsync(w.C, e => e.Action == "consent.refused"));        // the attempt is recorded where it came from
        Assert.Empty(await w.K.Store.Audit.FindAsync(w.A, e => e.Action == "consent.read"));            // and nothing was read
    }

    [Fact]
    public async Task AGrantToOneOrganisationIsInvisibleToTheOthersListings()
    {
        var w = await WorldAsync();
        await LinkGrantAsync(w, w.ToBank());

        Assert.Empty(await w.K.Consent.ListAsync(w.C));
        Assert.Single(await w.K.Consent.ListAsync(w.B));
    }

    // ---- who may grant and withdraw ----

    [Fact]
    public async Task ADirectFarmerGrantsAndWithdrawsInTheirOwnAccountButNobodyElseCan()
    {
        var w = await WorldAsync();
        var farmerOrg = await w.K.Organisations.CreateAsync("Abu Ali Farm", "farmer");
        var f = TenantScope.ForTenant(farmerOrg.Id);
        var self = (await w.K.Store.Farmers.FindAsync(f, _ => true)).Single();           // created from the farmer preset
        Assert.Equal(farmerOrg.Id, self.AccountOrganisationId);

        var terms = new GrantTerms(self.Id, w.Bank.Id, "Input finance", w.K.Clock.GetUtcNow().UtcDateTime.AddDays(60));
        var grant = await w.K.Consent.GrantAsync(f, "farmer-user", terms);
        Assert.Equal(("active", "account", "owner"), (grant.Status, grant.Channel, grant.Role));
        Assert.Equal("grantee", (await w.K.Consent.ListAsync(w.B)).Single().Role);

        // The bank and the company cannot withdraw it; the farmer can.
        await Assert.ThrowsAsync<NotFoundException>(() => w.K.Consent.WithdrawAsync(w.A, "company-admin", grant.GrantId));
        Assert.Equal(["not_your_consent"], await Refused(() => w.K.Consent.WithdrawAsync(w.B, "bank-admin", grant.GrantId)));
        Assert.Equal("withdrawn", (await w.K.Consent.WithdrawAsync(f, "farmer-user", grant.GrantId)).Status);
        Assert.Equal("withdrawn", (await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "u", grant.GrantId))).Reason);
    }

    [Fact]
    public async Task AManagingCompanyCannotGrantConsentOnAFarmersBehalf()
    {
        var w = await WorldAsync();

        // Ali has no account organisation, and even a farmer who has one is not the company's to speak for.
        Assert.Equal(["not_your_consent"], await Refused(() => w.K.Consent.GrantAsync(w.A, "company-admin", w.ToBank())));
        var farmerOrg = await w.K.Organisations.CreateAsync("Ali's account", "farmer");
        var withAccount = await w.K.FarmerService.CreateAsync(w.A, "u1", new NewFarmer("Hassan", AccountOrganisationId: farmerOrg.Id));
        Assert.Equal(["not_your_consent"], await Refused(() => w.K.Consent.GrantAsync(w.A, "company-admin", w.ToBank() with { FarmerId = withAccount.Id })));
        Assert.Empty(await w.K.Consent.ListAsync(w.B));
    }

    [Fact]
    public async Task AManagedFarmerWithAnAccountSeesTheGrantAndCanWithdrawItButTheCompanyCannot()
    {
        var w = await WorldAsync();
        var farmerOrg = await w.K.Organisations.CreateAsync("Ali's account", "farmer");
        var managed = await w.K.FarmerService.CreateAsync(w.A, "u1", new NewFarmer("Managed Ali", AccountOrganisationId: farmerOrg.Id));
        var farm = await w.K.Farms.CreateFarmAsync(w.A, "u1", new NewFarm("Managed farm", FarmerId: managed.Id));
        await w.K.Farms.CreateParcelAsync(w.A, "u1", new NewParcel(farm.Id, "M 0"));
        var f = TenantScope.ForTenant(farmerOrg.Id);
        var (grantId, _) = await LinkGrantAsync(w, w.ToBank() with { FarmerId = managed.Id });

        var seenByFarmer = (await w.K.Consent.ListAsync(f)).Single();
        Assert.Equal(("active", "farmer"), (seenByFarmer.Status, seenByFarmer.Role));

        Assert.Equal(["not_your_consent"], await Refused(() => w.K.Consent.WithdrawAsync(w.A, "company-admin", grantId)));
        Assert.Equal("withdrawn", (await w.K.Consent.WithdrawAsync(f, "farmer-user", grantId)).Status);
        Assert.Equal("withdrawn", (await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(w.B, "u", grantId))).Reason);
        Assert.Equal("withdrawn", (await w.K.Consent.ListAsync(w.A)).Single(g => g.GrantId == grantId).Status);

        // The farmer's account is not a receiving party, so it cannot read through the gate.
        Assert.Equal("no_such_grant", (await Assert.ThrowsAsync<ConsentDeniedException>(() => w.K.Gate.OpenAsync(f, "farmer-user", grantId))).Reason);
    }

    // ---- what can be asked for ----

    [Fact]
    public async Task ARequestIsCheckedBeforeAnythingIsCreated()
    {
        var w = await WorldAsync();
        var now = w.K.Clock.GetUtcNow().UtcDateTime;
        var ok = w.ToBank();

        Assert.Equal(["unknown_grantee"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { GranteeTenantId = "no-such-org" })));
        Assert.Equal(["grantee_is_owner"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { GranteeTenantId = w.Company.Id })));
        Assert.Equal(["purpose_required"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { Purpose = "  " })));
        Assert.Equal(["period_in_past"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { ValidUntil = now.AddDays(-1) })));
        Assert.Equal(["period_too_short"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { ValidUntil = now.AddHours(2) })));
        Assert.Equal(["period_too_long"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { ValidUntil = now.AddDays(731) })));
        Assert.Equal(["unknown_parcel"], await Refused(() => w.K.Consent.RequestAsync(w.A, "u", ok with { ParcelIds = [w.OmarParcel.Id] })));      // another farmer's parcel
        await Assert.ThrowsAsync<NotFoundException>(() => w.K.Consent.RequestAsync(w.A, "u", ok with { FarmerId = "nobody" }));
        Assert.Empty(await w.K.Store.ConsentRequests.FindAsync(w.A, _ => true));
    }

    [Fact]
    public async Task AFarmerCannotBeAskedAboutAnotherCompanysFarmers()
    {
        var w = await WorldAsync();
        var other = TenantScope.ForTenant(w.Insurer.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => w.K.Consent.RequestAsync(other, "u", w.ToBank()));
    }

    // ---- the ledger itself ----

    [Fact]
    public async Task TheLedgerOnlyEverGrowsAndTheOwnersCopyIsTheOneThatCounts()
    {
        var w = await WorldAsync();
        var (grantId, withdrawToken) = await LinkGrantAsync(w, w.ToBank());
        await w.K.Consent.WithdrawByTokenAsync(withdrawToken);

        var owner = await w.K.Store.ConsentEvents.FindAsync(w.A, e => e.GrantId == grantId);
        var bank = await w.K.Store.ConsentEvents.FindAsync(w.B, e => e.GrantId == grantId);

        Assert.Equal(["granted", "withdrawn"], owner.OrderBy(e => e.At).ThenBy(e => e.Event == "withdrawn").Select(e => e.Event));
        Assert.All(owner, e => Assert.False(e.Mirror));
        Assert.All(bank, e => Assert.True(e.Mirror));
        Assert.Null(bank.First().WithdrawTokenHash);                                                 // the secret never leaves the owner's tenant
        Assert.NotNull(owner.First(e => e.Event == "granted").WithdrawTokenHash);
        Assert.NotEqual(withdrawToken.Split('.')[^1], owner.First(e => e.Event == "granted").WithdrawTokenHash);   // and only its hash is kept
        Assert.All(owner.Concat(bank), e => Assert.Equal(ConsentDataCategories.All, e.DataCategories));
    }
}
