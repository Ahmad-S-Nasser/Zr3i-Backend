using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Kernel.Accounts;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class MePresetTests(MongoFixture mongo)
{
    private readonly MongoFixture _mongo = mongo;

    private static async Task<JsonElement> MeAsync(WebHarness web, string email, string accountType, string role)
    {
        var (_, invite) = await web.OrgWithInvitationAsync(email, role, "Org " + accountType, accountType);
        var token = await web.SignInAsync(email, await web.AcceptAsync(invite));
        var response = await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organisations")[0];
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("insurance")]
    [InlineData("company")]
    [InlineData("farmer")]
    public async Task EveryAccountTypeReturnsItsOwnPresetAndTheUsersRole(string typeKey)
    {
        await using var web = await WebHarness.StartAsync();
        var type = BuiltInAccountTypes.All.Single(t => t.Key == typeKey);
        var role = type.RoleKeys.Last();                                       // a role that is not always org-admin

        var org = await MeAsync(web, $"{role}@{typeKey}.example", typeKey, role);

        Assert.Equal(typeKey, org.GetProperty("accountType").GetString());
        Assert.Equal((type.LabelEn, type.LabelAr), (org.GetProperty("accountTypeLabelEn").GetString(), org.GetProperty("accountTypeLabelAr").GetString()));
        Assert.Equal(type.HomeView, org.GetProperty("homeView").GetString());
        Assert.Equal(type.Navigation, org.GetProperty("navigation").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(type.Modules, org.GetProperty("modules").EnumerateArray().Select(x => x.GetString()!));
        var roles = org.GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(role, Assert.Single(roles).GetProperty("key").GetString());
        Assert.False(string.IsNullOrWhiteSpace(roles[0].GetProperty("name").GetString()));
    }

    [Fact]
    public async Task TheProviderOrganisationReturnsTheAdminPreset()
    {
        await using var web = await WebHarness.StartAsync();
        var type = BuiltInAccountTypes.All.Single(t => t.Key == "zr3i-admin");

        var org = await MeAsync(web, "analyst@zr3i-admin.example", "zr3i-admin", "analyst");

        Assert.Equal(type.HomeView, org.GetProperty("homeView").GetString());
        Assert.Equal(type.Navigation, org.GetProperty("navigation").EnumerateArray().Select(x => x.GetString()!));
    }
}
