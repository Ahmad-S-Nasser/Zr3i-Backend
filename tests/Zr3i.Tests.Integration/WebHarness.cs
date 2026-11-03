using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel.Identity;
using Zr3i.Web;

namespace Zr3i.Tests.Integration;

/// <summary>The real API on a random local port, against a throwaway database.</summary>
public sealed class WebHarness : IAsyncDisposable
{
    public const string Password = "a long enough password";
    public const string AllowedOrigin = "http://localhost:5173";

    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
    private readonly string _database;
    private readonly DataStore _data;

    private WebHarness(Microsoft.AspNetCore.Builder.WebApplication app, string database, HttpClient http)
    {
        _app = app;
        _database = database;
        Http = http;
        _data = app.Services.GetRequiredService<DataStore>();
    }

    public HttpClient Http { get; }
    public string DatabaseName => _database;
    public static string MongoConnection => Environment.GetEnvironmentVariable("ZR3I_TEST_MONGO") ?? "mongodb://localhost:27017";
    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public static async Task<WebHarness> StartAsync(int authPerMinute = 1000)
    {
        var database = "zr3i_test_web_" + Guid.NewGuid().ToString("N");
        var app = WebApp.Build([], new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = MongoConnection,
            ["Mongo:Database"] = database,
            ["Mongo:MigrateOnStartup"] = "true",
            ["Auth:PasswordIterations"] = "1000",
            ["RateLimit:AuthPerMinute"] = authPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cors:Origins:0"] = AllowedOrigin,
            ["Urls"] = "http://127.0.0.1:0",
        });
        await app.StartAsync();
        var address = app.Urls.First();
        return new WebHarness(app, database, new HttpClient { BaseAddress = new Uri(address) });
    }

    /// <summary>Creates an organisation and an invitation for the email, as an admin would. Returns the org id and token.</summary>
    public async Task<(string OrgId, string Token)> OrgWithInvitationAsync(string email, string roleKey = "org-admin", string orgName = "Delta Agro", string accountType = "company")
    {
        var org = await Service<OrganisationService>().CreateAsync(orgName, accountType);
        var token = await Service<InvitationService>().InviteAsync(TenantScope.ForTenant(org.Id), "system", email, [roleKey]);
        return (org.Id, token);
    }

    /// <summary>Accepts an invitation over HTTP. Returns the authenticator secret used.</summary>
    public async Task<string> AcceptAsync(string token, string language = "en")
    {
        var secret = Totp.NewSecret();
        var response = await Http.PostAsJsonAsync("/api/invitations/accept", new
        {
            token, password = Password, language, totpSecret = secret, totpCode = Totp.CodeAt(secret, DateTimeOffset.UtcNow),
        });
        response.EnsureSuccessStatusCode();
        return secret;
    }

    /// <summary>Both sign-in steps over HTTP. Returns the bearer token.</summary>
    public async Task<string> SignInAsync(string email, string secret)
    {
        var first = await Http.PostAsJsonAsync("/api/auth/sign-in", new { email, password = Password });
        first.EnsureSuccessStatusCode();
        var challenge = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetString();

        var second = await Http.PostAsJsonAsync("/api/auth/verify", new { challenge, code = Totp.CodeAt(secret, DateTimeOffset.UtcNow) });
        second.EnsureSuccessStatusCode();
        return (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public HttpRequestMessage Request(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        var client = new MongoDB.Driver.MongoClient(Environment.GetEnvironmentVariable("ZR3I_TEST_MONGO") ?? "mongodb://localhost:27017");
        await client.DropDatabaseAsync(_database);
        _ = _data;
    }
}
