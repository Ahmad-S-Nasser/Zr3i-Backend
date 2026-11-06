using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Search;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>The top-bar search: the organisation's own farms, parcels, farmers and reports, by name, never ranked and never across organisations.</summary>
[Collection(MongoCollection.Name)]
public class SearchTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Search(WebHarness web, string orgId, string token, string q) => web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/search?q={Uri.EscapeDataString(q)}", token));

    private sealed record Org(string Id, string Token);

    private static async Task<Org> OrgAsync(WebHarness web, string email, string name, string type = "company", string role = "org-admin")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, role, name, type);
        return new Org(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    private static async Task SeedAsync(WebHarness web, string orgId, string farmName, params string[] parcelLabels)
    {
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm(farmName, Governorate: "Beheira"));
        foreach (var label in parcelLabels) await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
    }

    private static string[] Labels(JsonElement body, string kind) =>
        [.. body.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind).GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("label").GetString()!)];

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  b ")]
    public async Task AQueryUnderTwoCharactersIsRefused(string q)
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");

        var response = await Search(web, org.Id, org.Token, q);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query_too_short", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ItFindsFarmsAndParcelsIgnoringCaseAndOrdersThemByNameWithAFiveHitCap()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        await SeedAsync(web, org.Id, "Nile Terraces", "Plot G", "Plot B", "Plot E", "Plot A", "Plot D", "Plot F", "Plot C");
        await SeedAsync(web, org.Id, "Nile Delta Farm");

        var body = await Json(await Search(web, org.Id, org.Token, "NILE"));

        Assert.Equal(["Nile Delta Farm", "Nile Terraces"], Labels(body, "farms"));
        var parcels = await Json(await Search(web, org.Id, org.Token, "plot"));
        Assert.Equal(["Plot A", "Plot B", "Plot C", "Plot D", "Plot E"], Labels(parcels, "parcels"));
        var context = parcels.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("kind").GetString() == "parcels").GetProperty("hits")[0].GetProperty("context").GetString();
        Assert.Equal("Nile Terraces", context);
    }

    [Fact]
    public async Task AnotherOrganisationsRowsNeverAppearAndItsPathIsRefused()
    {
        await using var web = await WebHarness.StartAsync();
        var mine = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var theirs = await OrgAsync(web, "admin@other.example", "Other Co", "bank");
        await SeedAsync(web, mine.Id, "Shared Name Farm", "Shared Name Parcel");
        await SeedAsync(web, theirs.Id, "Shared Name Farm Two", "Shared Name Parcel Two");

        var body = await Json(await Search(web, mine.Id, mine.Token, "shared name"));

        Assert.Equal(["Shared Name Farm"], Labels(body, "farms"));
        Assert.Equal(["Shared Name Parcel"], Labels(body, "parcels"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Search(web, theirs.Id, mine.Token, "shared name")).StatusCode);
    }

    [Fact]
    public async Task AGroupTheRoleCannotReadIsAbsentNotEmpty()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        await SeedAsync(web, org.Id, "Nile Farm", "Nile Plot");
        var narrow = new Access(["portfolio:farms:read"], [], []);

        var groups = await web.Service<SearchService>().SearchAsync(TenantScope.ForTenant(org.Id), narrow, "nile");

        Assert.Equal(["farms", "parcels"], groups.Select(g => g.Kind).ToArray());
        var none = await web.Service<SearchService>().SearchAsync(TenantScope.ForTenant(org.Id), new Access([], [], []), "nile");
        Assert.Empty(none);
    }

    [Fact]
    public async Task FarmersAreFoundByNameButNeverByPhoneAndThePhoneIsNeverReturned()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        await web.Service<FarmerService>().CreateAsync(TenantScope.ForTenant(org.Id), "seed", new NewFarmer("Ali Mahmoud", Phone: "01012345678", ExternalId: "FRM-77"));

        var byName = await Json(await Search(web, org.Id, org.Token, "mahmoud"));
        var byPhone = await Search(web, org.Id, org.Token, "0101234");
        var byExternal = await Json(await Search(web, org.Id, org.Token, "frm-77"));

        Assert.Equal(["Ali Mahmoud"], Labels(byName, "farmers"));
        Assert.Empty(Labels(await Json(byPhone), "farmers"));
        Assert.Equal(["Ali Mahmoud"], Labels(byExternal, "farmers"));
        Assert.DoesNotContain("01012345678", byName.ToString());
    }

    [Fact]
    public async Task FrozenReportsAreFoundBySubject()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        var scope = TenantScope.ForTenant(org.Id);
        await web.Service<KernelStore>().FrozenReports.InsertAsync(scope, new Zr3i.Kernel.Reports.FrozenReport { Kind = "farm_risk", SubjectName = "Nile Terraces", SnapshotJson = "{}", SnapshotSha256 = "x", CreatedAt = DateTime.UtcNow });

        Assert.Equal(["Nile Terraces"], Labels(await Json(await Search(web, org.Id, org.Token, "terraces")), "reports"));
        Assert.Empty(Labels(await Json(await Search(web, other.Id, other.Token, "terraces")), "reports"));
    }

    [Fact]
    public async Task AResultCarriesNoRiskValue()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        await SeedAsync(web, org.Id, "Nile Farm", "Nile Plot");

        var text = (await Json(await Search(web, org.Id, org.Token, "nile"))).ToString().ToLowerInvariant();

        foreach (var word in new[] { "cri", "fri", "band", "risk", "score", "rank" }) Assert.DoesNotContain(word, text);
    }
}
