using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class FarmsTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("farms-tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("farms-tenant-b");

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return k;
    }

    private static async Task<(Portfolio Portfolio, Farm Farm, Parcel Parcel)> ChainAsync(KernelHarness k, TenantScope scope, string suffix = "")
    {
        var portfolio = await k.Farms.CreatePortfolioAsync(scope, "u1", new NewPortfolio("Delta book" + suffix));
        var farm = await k.Farms.CreateFarmAsync(scope, "u1", new NewFarm("Delta Agro Estate" + suffix, portfolio.Id, Governorate: "Kafr El Sheikh"));
        var parcel = await k.Farms.CreateParcelAsync(scope, "u1", new NewParcel(farm.Id, "ZR-KS-03" + suffix));
        return (portfolio, farm, parcel);
    }

    private static async Task<ValidationFailedException> Fails(Func<Task> action) => await Assert.ThrowsAsync<ValidationFailedException>(action);

    [Fact]
    public async Task APortfolioFarmParcelAndSeasonChainIsStoredUnderTheTenant()
    {
        var k = await NewAsync();
        var (portfolio, farm, parcel) = await ChainAsync(k, A);

        Assert.Equal("farms-tenant-a", portfolio.TenantId);
        Assert.Equal(portfolio.Id, farm.PortfolioId);
        Assert.Equal(farm.Id, parcel.FarmId);
        Assert.Null(parcel.CurrentBoundaryId);

        var season = await k.Farms.DeclareSeasonAsync(A, "u1",
            new NewParcelSeason(parcel.Id, "2026-27-winter", "beet", 10.0, "2026-10-01", "customer contract"));
        Assert.Equal(farm.Id, season.FarmId);
        Assert.Equal(10.0, season.DeclaredAreaFeddan);
        Assert.Equal("beet", season.DeclaredCrop);
    }

    [Fact]
    public async Task AParentFromAnotherTenantIsNotFound()
    {
        var k = await NewAsync();
        var (portfolioB, farmB, _) = await ChainAsync(k, B, "-b");

        await Assert.ThrowsAsync<NotFoundException>(() => k.Farms.CreateFarmAsync(A, "u1", new NewFarm("Cross", portfolioB.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Farms.CreateParcelAsync(A, "u1", new NewParcel(farmB.Id, "Cross")));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Farms.DeclareSeasonAsync(A, "u1", new NewParcelSeason("nope", "2026-27-winter", "beet", 1, null, null)));
        Assert.Empty(await k.Store.Farms.FindAsync(A, _ => true));
    }

    [Fact]
    public async Task ExternalIdsAreUniquePerTenantAndCollectionButNullsNeverCollide()
    {
        var k = await NewAsync();
        await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("One", ExternalId: "ERP-1"));

        var dup = await Fails(() => k.Farms.CreateFarmAsync(A, "u1", new NewFarm("Two", ExternalId: "ERP-1")));
        Assert.Equal(["duplicate_external_id"], dup.Codes);

        await k.Farms.CreateFarmAsync(B, "u1", new NewFarm("Other tenant", ExternalId: "ERP-1"));   // another tenant: fine
        await k.Farms.CreatePortfolioAsync(A, "u1", new NewPortfolio("Same id, other collection", "ERP-1"));
        await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("None 1"));
        await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("None 2", ExternalId: "   "));            // blank counts as none
        Assert.Equal(3, await k.Store.Farms.CountAsync(A, _ => true));   // One, None 1, None 2
    }

    [Fact]
    public async Task DeclaringTheSameSeasonTwiceUpdatesTheClaimAndKeepsOneRow()
    {
        var k = await NewAsync();
        var (_, _, parcel) = await ChainAsync(k, A);

        var first = await k.Farms.DeclareSeasonAsync(A, "u1", new NewParcelSeason(parcel.Id, "2026-27-winter", "beet", 10.0, "2026-10-01", "contract"));
        k.Clock.Advance(TimeSpan.FromDays(3));
        var second = await k.Farms.DeclareSeasonAsync(A, "u2", new NewParcelSeason(parcel.Id, "2026-27-winter", "beet", 9.5, "2026-10-04", "amended contract"));
        var other = await k.Farms.DeclareSeasonAsync(A, "u1", new NewParcelSeason(parcel.Id, "2027-summer", "maize", 9.5, null, null));

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, await k.Store.ParcelSeasons.CountAsync(A, _ => true));
        Assert.Equal(9.5, (await k.Store.ParcelSeasons.GetAsync(A, first.Id))!.DeclaredAreaFeddan);

        var audit = await k.Store.Audit.FindAsync(A, e => e.Action == "parcel_season.declared");
        Assert.Equal(3, audit.Count);
        Assert.Contains(audit, e => e.ActorUserId == "u2");
    }

    [Theory]
    [InlineData("", "beet", 1.0, "2026-10-01", "invalid_season")]
    [InlineData("Winter 2026", "beet", 1.0, "2026-10-01", "invalid_season")]
    [InlineData("2026-27-winter", "beet", -1.0, "2026-10-01", "invalid_declared_area")]
    [InlineData("2026-27-winter", "beet", 0.0, "2026-10-01", "invalid_declared_area")]
    [InlineData("2026-27-winter", "beet", 99999.0, "2026-10-01", "invalid_declared_area")]
    [InlineData("2026-27-winter", "beet", 1.0, "1 Oct", "invalid_sowing_date")]
    [InlineData("2026-27-winter", "Sugar Beet!", 1.0, "2026-10-01", "invalid_crop")]
    public async Task ADeclaredSeasonIsChecked(string season, string crop, double area, string sowing, string code)
    {
        var k = await NewAsync();
        var (_, _, parcel) = await ChainAsync(k, A);

        var failure = await Fails(() => k.Farms.DeclareSeasonAsync(A, "u1", new NewParcelSeason(parcel.Id, season, crop, area, sowing, null)));

        Assert.Equal([code], failure.Codes);
        Assert.Empty(await k.Store.ParcelSeasons.FindAsync(A, _ => true));
    }

    [Fact]
    public async Task NamesAndLabelsAreRequired()
    {
        var k = await NewAsync();
        var (_, farm, _) = await ChainAsync(k, A);

        Assert.Equal(["name_required"], (await Fails(() => k.Farms.CreatePortfolioAsync(A, "u1", new NewPortfolio("  ")))).Codes);
        Assert.Equal(["name_required"], (await Fails(() => k.Farms.CreateFarmAsync(A, "u1", new NewFarm("")))).Codes);
        Assert.Equal(["label_required"], (await Fails(() => k.Farms.CreateParcelAsync(A, "u1", new NewParcel(farm.Id, " ")))).Codes);
    }

    [Fact]
    public async Task CustomFieldsAreDefinedPerTenantAndEnforcedOnCreate()
    {
        var k = await NewAsync();
        await k.CustomFields.DefineAsync(A, CustomFieldEntities.Farm, "irrigation", "Irrigation", "الري", CustomFieldTypes.Choice, ["drip", "pivot"]);
        await k.CustomFields.DefineAsync(A, CustomFieldEntities.Farm, "contract_no", "Contract no.", "رقم العقد", CustomFieldTypes.Text, required: true);

        var farm = await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("F", CustomFields: new Dictionary<string, string?> { ["irrigation"] = "drip", ["contract_no"] = " CS-1 " }));
        Assert.Equal("drip", farm.CustomFields["irrigation"]);
        Assert.Equal("CS-1", farm.CustomFields["contract_no"]);

        var bad = await Fails(() => k.Farms.CreateFarmAsync(A, "u1", new NewFarm("G", CustomFields: new Dictionary<string, string?> { ["irrigation"] = "flood" })));
        Assert.Equal(["custom_field:contract_no:required", "custom_field:irrigation:invalid_choice"], bad.Codes.Order());

        // The same farm in another tenant, which defined nothing, has no such rules and does not know these keys.
        await k.Farms.CreateFarmAsync(B, "u1", new NewFarm("H"));
        var unknown = await Fails(() => k.Farms.CreateFarmAsync(B, "u1", new NewFarm("I", CustomFields: new Dictionary<string, string?> { ["irrigation"] = "drip" })));
        Assert.Equal(["custom_field:irrigation:unknown_field"], unknown.Codes);
    }

    [Fact]
    public async Task ACustomFieldDefinitionIsChecked()
    {
        var k = await NewAsync();
        await k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "soil", "Soil", "التربة", CustomFieldTypes.Text);

        async Task<string[]> Codes(Func<Task> a) => [.. (await Fails(a)).Codes];
        Assert.Equal(["duplicate_key"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "soil", "Soil 2", "التربة 2", CustomFieldTypes.Text)));
        Assert.Equal(["invalid_key"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "Bad Key", "x", "x", CustomFieldTypes.Text)));
        Assert.Equal(["invalid_entity"], await Codes(() => k.CustomFields.DefineAsync(A, "tractor", "a", "x", "x", CustomFieldTypes.Text)));
        Assert.Equal(["invalid_type"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "a", "x", "x", "blob")));
        Assert.Equal(["options_required"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "a", "x", "x", CustomFieldTypes.Choice)));
        Assert.Equal(["label_en_required"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "a", "", "x", CustomFieldTypes.Text)));
        Assert.Equal(["label_ar_required"], await Codes(() => k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "a", "x", " ", CustomFieldTypes.Text)));
    }
}
