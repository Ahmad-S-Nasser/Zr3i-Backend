using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Kernel.Accounts;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class MethodApiTests
{
    private const string Url = "/api/methods/ZR3I-R0-2026-01";
    private const string Admin = "/api/platform/methods/ZR3I-R0-2026-01/1";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> ProviderAsync(WebHarness web) =>
        await web.SignInAsync("founder@zr3i.example", await web.AcceptAsync((await web.Service<PlatformService>().BootstrapAsync("founder@zr3i.example"))!));

    [Fact]
    public async Task EveryoneSignedInCanReadHowSettledEachRuleIs()
    {
        await using var web = await WebHarness.StartAsync();
        var (_, invite) = await web.OrgWithInvitationAsync("viewer@delta.example", "viewer");
        var user = await web.SignInAsync("viewer@delta.example", await web.AcceptAsync(invite));

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(Url)).StatusCode);
        var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/methods", user)));
        Assert.Equal(("ZR3I-R0-2026-01", "draft", 8), (list[0].GetProperty("methodId").GetString(), list[0].GetProperty("state").GetString(), list[0].GetProperty("parameters").GetProperty("undecided").GetInt32()));

        var detail = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, Url, user)));
        var offset = detail.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "area_gap.boundary_offset_m");
        Assert.Equal(("2", "starting_rule"), (offset.GetProperty("value").GetString(), offset.GetProperty("status").GetString()));
        Assert.Contains("PE 3A.2", offset.GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sealVerified").ValueKind);                         // a draft has no seal
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/methods/NO-SUCH", user))).StatusCode);
    }

    [Fact]
    public async Task OnlyZr3iAdministratorsCanEditAndEditsAreChecked()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await ProviderAsync(web);
        var (_, invite) = await web.OrgWithInvitationAsync("admin@delta.example");
        var ordinary = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(invite));
        var body = new { value = "6", status = "starting_rule", source = "Partner proposal" };

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Put, $"{Admin}/parameters/crop.min_valid_scenes", ordinary, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, $"{Admin}/freeze", ordinary, new { approvedBy = "x", approvedKeys = Array.Empty<string>() }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.PutAsJsonAsync($"{Admin}/parameters/crop.min_valid_scenes", body)).StatusCode);

        var ok = await web.Http.SendAsync(web.Request(HttpMethod.Put, $"{Admin}/parameters/crop.min_valid_scenes", admin, body));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("6", (await Json(ok)).GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "crop.min_valid_scenes").GetProperty("value").GetString());

        var bad = await web.Http.SendAsync(web.Request(HttpMethod.Put, $"{Admin}/parameters/crop.min_valid_scenes", admin, new { value = "", status = "starting_rule", source = "x" }));
        Assert.Equal("value_required", (await Json(bad)).GetProperty("error").GetString());
        var early = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"{Admin}/freeze", admin, new { approvedBy = "Dr. Partner", approvedKeys = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("undecided:verdict.precedence", (await Json(early)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task AMethodIsFrozenOnlyOnceEverythingIsSettledAndApprovedByNameAndTheSealVerifies()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await ProviderAsync(web);
        var draft = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, Url, admin)));

        foreach (var key in draft.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("status").GetString() == "undecided").Select(p => p.GetProperty("key").GetString()))
            Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Request(HttpMethod.Put, $"{Admin}/parameters/{key}", admin, new { value = "stand-in", status = "starting_rule", source = "test" }))).StatusCode);
        var provisional = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, Url, admin)))).GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("status").GetString() == "starting_rule").Select(p => p.GetProperty("key").GetString()).ToArray();

        var frozen = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Post, $"{Admin}/freeze", admin, new { approvedBy = "Dr. Partner", approvedKeys = provisional })));

        Assert.Equal(("frozen", "Dr. Partner", true), (frozen.GetProperty("state").GetString(), frozen.GetProperty("approvedBy").GetString(), frozen.GetProperty("sealVerified").GetBoolean()));
        Assert.Matches("^[0-9A-F]{64}$", frozen.GetProperty("sealSha256").GetString()!);
        var again = await web.Http.SendAsync(web.Request(HttpMethod.Put, $"{Admin}/parameters/area_gap.flag_multiplier", admin, new { value = "9", status = "specified", source = "x" }));
        Assert.Equal("method_frozen", (await Json(again)).GetProperty("error").GetString());

        var next = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"{Admin}/new-draft", admin));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        var created = await Json(next);
        Assert.Equal(("draft", 2), (created.GetProperty("state").GetString(), created.GetProperty("version").GetInt32()));
    }
}
