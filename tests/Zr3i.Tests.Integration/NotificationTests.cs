using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Notifications;
using Zr3i.Worker;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>A person's notifications: only their own, written for the right people when something happens, and never carrying the data itself.</summary>
[Collection(MongoCollection.Name)]
public class NotificationTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));
    private static Task<HttpResponseMessage> Post(WebHarness web, string url, string token, object body) => web.Http.SendAsync(web.Request(HttpMethod.Post, url, token, body));

    private sealed record Person(string OrgId, string Email, string UserId, string Secret, string Token);

    private static async Task<Person> PersonAsync(WebHarness web, string orgId, string email, string role)
    {
        var invite = await web.Service<InvitationService>().InviteAsync(TenantScope.ForTenant(orgId), "system", email, [role]);
        var secret = await web.AcceptAsync(invite);
        var token = await web.SignInAsync(email, secret);
        var me = await Json(await Get(web, "/api/me", token));
        return new Person(orgId, email, me.GetProperty("id").GetString()!, secret, token);
    }

    private static async Task<Person> OrgAdminAsync(WebHarness web, string email, string orgName, string type = "company")
    {
        var org = await web.Service<OrganisationService>().CreateAsync(orgName, type);
        return await PersonAsync(web, org.Id, email, "org-admin");
    }

    private static async Task<JsonElement> InboxAsync(WebHarness web, Person p, bool unread = false) =>
        await Json(await Get(web, $"/api/organisations/{p.OrgId}/notifications{(unread ? "?unread=true" : "")}", p.Token));

    private static async Task<string[]> CodesAsync(WebHarness web, Person p) =>
        [.. (await InboxAsync(web, p)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).Reverse()];

    private static NotificationService Notes(WebHarness web) => web.Service<NotificationService>();

    // ---- who sees what ----

    [Fact]
    public async Task APersonSeesOnlyTheirOwnNotificationsAndAnotherOrganisationSeesNone()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var viewer = await PersonAsync(web, admin.OrgId, "viewer@delta.example", "viewer");
        var other = await OrgAdminAsync(web, "admin@other.example", "Other Co");
        await Notes(web).NotifyAsync(TenantScope.ForTenant(admin.OrgId), [admin.UserId], NotificationCodes.ReportReady, "report", "r1", "tranche");

        var mine = await InboxAsync(web, admin);
        Assert.Equal(1, mine.GetProperty("unreadCount").GetInt32());
        Assert.Equal("report.ready", mine.GetProperty("items")[0].GetProperty("code").GetString());
        Assert.Equal(0, (await InboxAsync(web, viewer)).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await InboxAsync(web, other)).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{admin.OrgId}/notifications", other.Token)).StatusCode);
    }

    [Fact]
    public async Task MarkingReadChangesOnlyTheCallersOwnRows()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var viewer = await PersonAsync(web, admin.OrgId, "viewer@delta.example", "viewer");
        var scope = TenantScope.ForTenant(admin.OrgId);
        await Notes(web).NotifyAsync(scope, [admin.UserId, viewer.UserId], NotificationCodes.ImportCommitted, "import_batch", "b1");
        var adminRow = (await InboxAsync(web, admin)).GetProperty("items")[0].GetProperty("id").GetString()!;

        var foreign = await Json(await Post(web, $"/api/organisations/{admin.OrgId}/notifications/read", viewer.Token, new { ids = new[] { adminRow } }));
        Assert.Equal(0, foreign.GetProperty("marked").GetInt32());
        Assert.Equal(1, (await InboxAsync(web, admin)).GetProperty("unreadCount").GetInt32());

        var all = await Json(await Post(web, $"/api/organisations/{admin.OrgId}/notifications/read", viewer.Token, new { all = true }));
        Assert.Equal(1, all.GetProperty("marked").GetInt32());
        Assert.Equal(0, (await InboxAsync(web, viewer)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await InboxAsync(web, admin)).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task TheListIsNewestFirstAndTheUnreadFilterHidesReadOnes()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var scope = TenantScope.ForTenant(admin.OrgId);
        foreach (var code in new[] { NotificationCodes.ImportValidated, NotificationCodes.ImportCommitted, NotificationCodes.ReportReady })
        {
            await Notes(web).NotifyAsync(scope, [admin.UserId], code, "x", "1");
            await Task.Delay(25);
        }

        var all = (await InboxAsync(web, admin)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Equal(["report.ready", "import.committed", "import.validated"], all);

        var newest = (await InboxAsync(web, admin)).GetProperty("items")[0].GetProperty("id").GetString()!;
        await Post(web, $"/api/organisations/{admin.OrgId}/notifications/read", admin.Token, new { ids = new[] { newest } });
        var unread = await InboxAsync(web, admin, unread: true);
        Assert.Equal(2, unread.GetProperty("items").GetArrayLength());
        Assert.Equal(2, unread.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task AdminsAreTheOnlyOnesToldAboutAccountEvents()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var analyst = await PersonAsync(web, admin.OrgId, "analyst@delta.example", "analyst");

        await Notes(web).NotifyAdminsAsync(TenantScope.ForTenant(admin.OrgId), NotificationCodes.AccountLocked, "user", analyst.UserId);

        Assert.Equal(["account.locked"], await CodesAsync(web, admin));
        Assert.Empty(await CodesAsync(web, analyst));
    }

    [Fact]
    public async Task ANotificationNamesTheThingButNeverCopiesItsData()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        await Notes(web).NotifyAsync(TenantScope.ForTenant(admin.OrgId), [admin.UserId], NotificationCodes.ReportReady, "report", "r1", new string('x', 500));

        var item = (await InboxAsync(web, admin)).GetProperty("items")[0];
        Assert.Equal(120, item.GetProperty("detail").GetString()!.Length);
        Assert.Equal(["code", "createdAt", "detail", "id", "objectId", "objectType", "read"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    // ---- consent ----

    private sealed record ConsentWorld(Person Company, Person Bank, Person Farmer, string FarmerId);

    private static async Task<ConsentWorld> ConsentWorldAsync(WebHarness web)
    {
        var company = await OrgAdminAsync(web, "admin@agricash.example", "AgriCash");
        var bank = await OrgAdminAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var farmerPerson = await OrgAdminAsync(web, "ali@farm.example", "Abu Ali Farm", "farmer");
        var farmer = await web.Service<FarmerService>().CreateAsync(TenantScope.ForTenant(company.OrgId), company.UserId, new NewFarmer("Ali Mahmoud", AccountOrganisationId: farmerPerson.OrgId));
        return new ConsentWorld(company, bank, farmerPerson, farmer.Id);
    }

    private static GrantTerms Terms(ConsentWorld w) => new(w.FarmerId, w.Bank.OrgId, "Verify input finance", DateTime.UtcNow.AddDays(90));

    [Fact]
    public async Task ARequestTellsTheFarmersOwnPeopleAndTheAnswerTellsTheAskerAndTheBank()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await ConsentWorldAsync(web);
        var consent = web.Service<ConsentService>();

        var (_, token) = await consent.RequestAsync(TenantScope.ForTenant(w.Company.OrgId), w.Company.UserId, Terms(w));
        Assert.Equal(["consent.requested"], await CodesAsync(web, w.Farmer));
        Assert.Empty(await CodesAsync(web, w.Bank));

        await consent.RespondAsync(token, true, "en");
        Assert.Equal(["consent.granted"], await CodesAsync(web, w.Company));
        Assert.Equal(["consent.granted"], await CodesAsync(web, w.Bank));
        var bankItem = (await InboxAsync(web, w.Bank)).GetProperty("items")[0];
        Assert.Equal("Verify input finance", bankItem.GetProperty("detail").GetString());
        Assert.DoesNotContain("Ali", bankItem.ToString());
    }

    [Fact]
    public async Task ADeclineTellsTheAskerAndNobodyElse()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await ConsentWorldAsync(web);
        var consent = web.Service<ConsentService>();
        var (_, token) = await consent.RequestAsync(TenantScope.ForTenant(w.Company.OrgId), w.Company.UserId, Terms(w));

        await consent.RespondAsync(token, false, "en");

        Assert.Equal(["consent.declined"], await CodesAsync(web, w.Company));
        Assert.Empty(await CodesAsync(web, w.Bank));
    }

    [Fact]
    public async Task AWithdrawalTellsTheBankAndTheHolderOfTheRecords()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await ConsentWorldAsync(web);
        var consent = web.Service<ConsentService>();
        var (_, token) = await consent.RequestAsync(TenantScope.ForTenant(w.Company.OrgId), w.Company.UserId, Terms(w));
        var granted = await consent.RespondAsync(token, true, "en");

        await consent.WithdrawByTokenAsync(granted.WithdrawToken!);

        Assert.Contains("consent.withdrawn", await CodesAsync(web, w.Bank));
        Assert.Contains("consent.withdrawn", await CodesAsync(web, w.Company));
        Assert.DoesNotContain("consent.withdrawn", await CodesAsync(web, w.Farmer));
    }

    [Fact]
    public async Task AGrantFromTheFarmersOwnAccountTellsTheBank()
    {
        await using var web = await WebHarness.StartAsync();
        var w = await ConsentWorldAsync(web);
        var farmerRecord = (await web.Service<FarmerService>().ListAsync(TenantScope.ForTenant(w.Farmer.OrgId))).Single();

        await web.Service<ConsentService>().GrantAsync(TenantScope.ForTenant(w.Farmer.OrgId), w.Farmer.UserId, Terms(w) with { FarmerId = farmerRecord.Id });

        Assert.Equal(["consent.granted"], await CodesAsync(web, w.Bank));
    }

    // ---- account ----

    [Fact]
    public async Task ReEnrolmentTellsTheAdminsButNotTheMember()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var member = await PersonAsync(web, admin.OrgId, "analyst@delta.example", "analyst");
        var link = (await Json(await Post(web, $"/api/organisations/{admin.OrgId}/members/{member.UserId}/reset-authenticator", admin.Token, new { }))).GetProperty("token").GetString()!;
        var fresh = Totp.NewSecret();

        (await web.Http.PostAsJsonAsync("/api/authenticator-reset", new { token = link, password = WebHarness.Password, totpSecret = fresh, totpCode = Totp.CodeAt(fresh, DateTimeOffset.UtcNow) })).EnsureSuccessStatusCode();

        Assert.Equal(["authenticator.reset_completed"], await CodesAsync(web, admin));
        Assert.Empty(await CodesAsync(web, await SignedIn(web, member, fresh)));
    }

    private static async Task<Person> SignedIn(WebHarness web, Person p, string secret) => p with { Token = await web.SignInAsync(p.Email, secret) };

    [Fact]
    public async Task ALockedAccountTellsTheOrganisationsAdmins()
    {
        await using var web = await WebHarness.StartAsync();
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var member = await PersonAsync(web, admin.OrgId, "analyst@delta.example", "analyst");

        for (var i = 0; i < SignInService.MaxFailures; i++)
            await web.Http.PostAsJsonAsync("/api/auth/sign-in", new { email = member.Email, password = "wrong password entirely" });

        Assert.Equal(["account.locked"], await CodesAsync(web, admin));
        Assert.Empty(await CodesAsync(web, member));
    }

    // ---- imports (through a real worker) ----

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

    private static HttpRequestMessage Upload(WebHarness web, Person who, string csv)
    {
        var request = web.Request(HttpMethod.Post, $"/api/organisations/{who.OrgId}/imports?fileName=farms.csv", who.Token);
        request.Content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(csv));
        request.Content.Headers.ContentType = new("text/csv");
        return request;
    }

    private static async Task WaitForCodeAsync(WebHarness web, Person who, string code)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if ((await CodesAsync(web, who)).Contains(code)) return;
            await Task.Delay(150);
        }

        throw new TimeoutException($"No {code} notification arrived.");
    }

    [Fact]
    public async Task AnImportTellsTheUploaderWhenItIsValidatedAndWhenItIsCommitted()
    {
        await using var web = await WebHarness.StartAsync();
        using var worker = StartWorker(web);
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");
        var viewer = await PersonAsync(web, admin.OrgId, "viewer@delta.example", "viewer");
        var accepted = await web.Http.SendAsync(Upload(web, admin, ImportCsv.Build(Enumerable.Range(0, 3).Select(i => ImportCsv.Row(i)))));
        var id = (await Json(accepted)).GetProperty("id").GetString()!;

        await WaitForCodeAsync(web, admin, "import.validated");
        (await Post(web, $"/api/organisations/{admin.OrgId}/imports/{id}/commit", admin.Token, new { })).EnsureSuccessStatusCode();
        await WaitForCodeAsync(web, admin, "import.committed");

        Assert.Empty(await CodesAsync(web, viewer));
        Assert.Equal("farms.csv", (await InboxAsync(web, admin)).GetProperty("items")[0].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ARejectedFileTellsTheUploader()
    {
        await using var web = await WebHarness.StartAsync();
        using var worker = StartWorker(web);
        var admin = await OrgAdminAsync(web, "admin@delta.example", "Delta Agro");

        (await web.Http.SendAsync(Upload(web, admin, "not,the,right,columns\n1,2,3,4\n"))).EnsureSuccessStatusCode();

        await WaitForCodeAsync(web, admin, "import.rejected");
    }
}
