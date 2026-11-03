using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Imports;
using Zr3i.Worker;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>The whole path over HTTP: the API takes the upload, a real worker host does the work, the client polls and reads the report.</summary>
[Collection(MongoCollection.Name)]
public class ImportApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static IHost StartWorker(WebHarness web)
    {
        var host = WorkerHost.Build([], new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = WebHarness.MongoConnection,
            ["Mongo:Database"] = web.DatabaseName,
            ["Worker:Concurrency"] = "1",
            ["Worker:PollSeconds"] = "0.1",
        });
        host.Start();
        return host;
    }

    private static async Task<JsonElement> WaitForAsync(WebHarness web, string url, string token, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var response = await web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await Json(response);
            if (done(body)) return body;
            await Task.Delay(100);
        }

        throw new TimeoutException($"{url} did not reach the expected state.");
    }

    private static HttpRequestMessage Upload(WebHarness web, string orgId, string token, string csv, string fileName = "farms.csv")
    {
        var request = web.Request(HttpMethod.Post, $"/api/organisations/{orgId}/imports?fileName={Uri.EscapeDataString(fileName)}", token);
        request.Content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(csv));
        request.Content.Headers.ContentType = new("text/csv");
        return request;
    }

    private static async Task<(string OrgId, string Admin)> AdminAsync(WebHarness web, string email, string orgName)
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, orgName: orgName);
        return (orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    [Fact]
    public async Task UploadValidateReadTheReportCommitAndReimportOverHttp()
    {
        await using var web = await WebHarness.StartAsync();
        using var worker = StartWorker(web);
        var (orgId, admin) = await AdminAsync(web, "admin@delta.example", "Delta Agro");
        var rows = Enumerable.Range(0, 8).Select(i => ImportCsv.Row(i)).ToList();
        rows[2]["declared_area_feddan"] = "ten";
        var csv = ImportCsv.Build(rows);

        // Upload returns at once and the batch moves on its own.
        var accepted = await web.Http.SendAsync(Upload(web, orgId, admin, csv));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await Json(accepted)).GetProperty("id").GetString()!;
        var url = $"/api/organisations/{orgId}/imports/{id}";

        var validated = await WaitForAsync(web, url, admin, b => b.GetProperty("status").GetString() is "validated" or "failed");
        Assert.Equal(("validated", 8, 7, 1), (validated.GetProperty("status").GetString(), validated.GetProperty("totalRows").GetInt32(),
            validated.GetProperty("validRows").GetInt32(), validated.GetProperty("invalidRows").GetInt32()));

        // The error report, as JSON and as a CSV download.
        var errors = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "/errors", admin)));
        var row = errors.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal((4, "P-00002", "declared_area_feddan", "invalid_number"),
            (row.GetProperty("line").GetInt32(), row.GetProperty("parcelExternalId").GetString(),
             row.GetProperty("errors")[0].GetProperty("field").GetString(), row.GetProperty("errors")[0].GetProperty("code").GetString()));
        var download = await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "/errors.csv", admin));
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("4,P-00002,F-0000,declared_area_feddan,invalid_number", await download.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Commit, then wait for the worker.
        var commit = await web.Http.SendAsync(web.Request(HttpMethod.Post, url + "/commit", admin));
        Assert.Equal(HttpStatusCode.Accepted, commit.StatusCode);
        var committed = await WaitForAsync(web, url, admin, b => b.GetProperty("status").GetString() == "committed");
        Assert.Equal((7, 0, 0), (committed.GetProperty("created").GetInt32(), committed.GetProperty("updated").GetInt32(), committed.GetProperty("failed").GetInt32()));
        Assert.Equal(7, await web.Service<KernelStore>().Parcels.CountAsync(TenantScope.ForTenant(orgId), _ => true));

        // The same file again: validated, committed, nothing created.
        var again = await Json(await web.Http.SendAsync(Upload(web, orgId, admin, csv)));
        var url2 = $"/api/organisations/{orgId}/imports/{again.GetProperty("id").GetString()}";
        await WaitForAsync(web, url2, admin, b => b.GetProperty("status").GetString() == "validated");
        await web.Http.SendAsync(web.Request(HttpMethod.Post, url2 + "/commit", admin));
        var second = await WaitForAsync(web, url2, admin, b => b.GetProperty("status").GetString() == "committed");
        Assert.Equal((0, 7), (second.GetProperty("created").GetInt32(), second.GetProperty("unchanged").GetInt32()));
        Assert.Equal(7, await web.Service<KernelStore>().Parcels.CountAsync(TenantScope.ForTenant(orgId), _ => true));

        var list = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/imports", admin)));
        Assert.Equal(2, list.GetArrayLength());
        await worker.StopAsync();
    }

    [Fact]
    public async Task OnlyPeopleWithTheRightPermissionCanImportAndOthersAreTurnedAwayAndAudited()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgA, adminA) = await AdminAsync(web, "admin-a@example.test", "Org A");
        var (_, adminB) = await AdminAsync(web, "admin-b@example.test", "Org B");
        var viewerInvite = await web.Service<Zr3i.Kernel.Identity.InvitationService>().InviteAsync(TenantScope.ForTenant(orgA), "system", "viewer@example.test", ["viewer"]);
        var viewer = await web.SignInAsync("viewer@example.test", await web.AcceptAsync(viewerInvite));
        var csv = ImportCsv.Build(Enumerable.Range(0, 2).Select(i => ImportCsv.Row(i)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(Upload(web, orgA, "", csv))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Upload(web, orgA, viewer, csv))).StatusCode);       // read-only role
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(Upload(web, orgA, adminB, csv))).StatusCode);       // another organisation's admin

        var uploaded = await Json(await web.Http.SendAsync(Upload(web, orgA, adminA, csv)));
        var url = $"/api/organisations/{orgA}/imports/{uploaded.GetProperty("id").GetString()}";
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, viewer))).StatusCode);   // a viewer may read
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Post, url + "/commit", viewer))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, adminB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "/errors.csv", adminB))).StatusCode);

        var denied = await web.Service<KernelStore>().Audit.FindAsync(TenantScope.ForTenant(orgA), e => e.Action == "access.denied");
        Assert.Equal(5, denied.Count);
    }

    [Fact]
    public async Task ProblemsComeBackAsStableCodes()
    {
        await using var web = await WebHarness.StartAsync();
        var (orgId, admin) = await AdminAsync(web, "admin@delta.example", "Delta Agro");

        var empty = await web.Http.SendAsync(Upload(web, orgId, admin, ""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("empty_file", (await Json(empty)).GetProperty("error").GetString());

        var tooBig = await web.Http.SendAsync(Upload(web, orgId, admin, new string('x', ImportService.MaxBytes + 1)));
        Assert.Equal("file_too_large", (await Json(tooBig)).GetProperty("error").GetString());

        var unknown = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{orgId}/imports/does-not-exist", admin));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", (await Json(unknown)).GetProperty("error").GetString());

        var accepted = await Json(await web.Http.SendAsync(Upload(web, orgId, admin, ImportCsv.Build([ImportCsv.Row(0)]))));
        var early = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{orgId}/imports/{accepted.GetProperty("id").GetString()}/commit", admin));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);                       // no worker is running, so it is not validated yet
        Assert.Equal("not_validated", (await Json(early)).GetProperty("error").GetString());
    }
}
