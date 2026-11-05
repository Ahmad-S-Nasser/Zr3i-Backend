using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Zr3i.Web;

// Seeds a local DEVELOPMENT database: one account for every built-in role of every built-in account type (the system's own presets),
// plus synthetic Delta Agro Estate farm data in the Company organisation. Writes the accounts to a JSON file for the frontend's
// dev-only login page. The file holds a password and authenticator secrets, so it must stay out of git.
//
//   dotnet run --project tools/Zr3i.DevSeed -- --out <ABSOLUTE path to Zr3i-Frontend/src/dev/devPersonas.json> [--mongo mongodb://localhost:27017] [--db zr3i_dev]
// (dotnet run resolves a relative path from the project folder, so use an absolute one.)
//
// Drop the database first for a clean reset (mongosh: db.getSiblingDB('zr3i_dev').dropDatabase()). Running twice adds a second set of organisations.

const string DevPassword = "a long enough password";   // development only; the same constant the tests use

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var outPath = Arg("out", "devPersonas.json");
var mongo = Arg("mongo", "mongodb://localhost:27017");
var database = Arg("db", "zr3i_dev");
if (!database.StartsWith("zr3i_dev", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Refusing to seed '{database}': the dev database name must start with zr3i_dev.");
    return 1;
}

var app = WebApp.Build([], new Dictionary<string, string?>
{
    ["Mongo:ConnectionString"] = mongo, ["Mongo:Database"] = database, ["Mongo:MigrateOnStartup"] = "true",
    ["RateLimit:AuthPerMinute"] = "1000", ["Urls"] = "http://127.0.0.1:0",
});
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
T S<T>() where T : notnull => app.Services.GetRequiredService<T>();

var orgNames = new Dictionary<string, string>
{
    ["zr3i-admin"] = "Zr3i", ["bank"] = "Delta Bank", ["insurance"] = "Nile Insurance", ["company"] = "Delta Agro Estate", ["farmer"] = "Hassan Farm",
};

var personas = new List<object>();
string companyOrgId = "";
foreach (var type in BuiltInAccountTypes.All)
{
    TenantScope scope;
    string orgId;
    if (type.Key == "zr3i-admin")
    {
        var provider = await S<OrganisationService>().EnsureProviderAsync();
        orgId = provider.Id;
        scope = PlatformTenant.Scope;
    }
    else
    {
        var org = await S<OrganisationService>().CreateAsync(orgNames[type.Key], type.Key);
        orgId = org.Id;
        scope = TenantScope.ForTenant(org.Id);
    }

    if (type.Key == "company") companyOrgId = orgId;

    foreach (var roleKey in type.RoleKeys)
    {
        var email = $"{roleKey}@{type.Key}.example";
        var token = await S<InvitationService>().InviteAsync(scope, "system", email, [roleKey]);
        var secret = Totp.NewSecret();
        var accepted = await http.PostAsJsonAsync("/api/invitations/accept", new { token, password = DevPassword, language = "en", totpSecret = secret, totpCode = Totp.CodeAt(secret, DateTimeOffset.UtcNow) });
        accepted.EnsureSuccessStatusCode();

        var role = RoleTemplates.All.Single(r => r.Key == roleKey);
        personas.Add(new
        {
            id = $"{type.Key}:{roleKey}", accountType = type.Key, accountTypeLabel = type.LabelEn, orgId, orgName = orgNames[type.Key],
            role = roleKey, roleName = role.Name, permissions = role.Permissions, modules = type.Modules, home = type.HomeView, navigation = type.Navigation, email, totpSecret = secret,
        });
    }
}

await SeedCompanyDataAsync(TenantScope.ForTenant(companyOrgId));

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, JsonSerializer.Serialize(new { password = DevPassword, personas }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
Console.WriteLine($"Seeded {personas.Count} accounts in '{database}'. Wrote {outPath}");
await app.StopAsync();
return 0;

// Synthetic data for the Company organisation: the Delta Agro Estate farm of the methodology's worked example.
async Task SeedCompanyDataAsync(TenantScope scope)
{
    var farms = S<FarmService>();
    var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0404", Governorate: "Kafr El Sheikh"));
    await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0405", Governorate: "Gharbia"));
    var captured = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    var recorder = S<IndicatorRecorder>();
    var risk = S<RiskService>();

    (string Label, double Area, double Cri, string Crop)[] delta =
    [
        ("ZR-KS-01", 62.4, 19.0, "wheat"), ("ZR-KS-02", 48.1, 26.0, "wheat"), ("ZR-KS-03", 55.7, 50.3, "maize"), ("ZR-KS-04", 71.2, 33.0, "beet"),
        ("ZR-KS-05", 39.8, 22.0, "wheat"), ("ZR-KS-06", 44.3, 58.0, "maize"), ("ZR-KS-07", 52.6, 71.0, "rice"), ("ZR-KS-08", 33.9, 29.0, "beet"), ("ZR-KS-09", 20.0, 41.0, "wheat"),
    ];
    double[] weights = [25, 20, 15, 15, 10, 8, 7];
    string[] keys = ["water_security", "climate_exposure", "soil_and_land", "yield_stability", "management_quality", "tenure_and_rights", "peer_and_context"];
    string[] features = ["ndmi_min", "heat_stress", "ndvi_integral", "yield_history", "ndvi_integral", "area_variance_pct", "ndvi_cohort_percentile"];

    foreach (var (label, area, cri, crop) in delta)
    {
        var parcel = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
        var side = Math.Sqrt(area * 4200.83);
        var corners = new[] { (303800.0, 3409200.0), (303800 + side, 3409200.0), (303800 + side, 3409200 + side), (303800.0, 3409200 + side), (303800.0, 3409200.0) };
        var ring = corners.Select(c => { var (lon, lat) = Epsg32636.ToLonLat(c.Item1, c.Item2); return new LonLat(lon, lat); });
        await S<BoundaryService>().AddAsync(scope, "seed", parcel.Id, PolygonInput.FromExterior(ring), new BoundaryProvenance("survey", "dev seed", captured, 1));
        var season = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, "2026-27", crop, area, "2026-10-01", "contract"));

        if (label == "ZR-KS-03")
        {
            // The worked example's parcel, with its real features.
            await risk.RecordCriAsync(scope, "seed", season.Id, new CriFeatures(24.0, 0.58, 46.0, 12.0, 34.0, [73.5, 79, 97, 100, 100, 104, 108, 112], 0.9));
        }
        else
        {
            // Synthetic: every component carries the parcel's CRI so the farm roll-up matches the methodology example.
            var components = keys.Select((k, i) => new IndicatorComponent(k, weights[i], cri, true, features[i], null)).ToList();
            await recorder.RecordAsync(scope, "seed", new IndicatorDraft("CRI", "parcel_season", season.Id, "FRI-2026-01", 1, components, cri, 0.9, false, null, 8));
        }
    }

    // Synthetic reference distributions so parcel bands appear (evenly spread working scores).
    var scores = Enumerable.Range(0, 100).Select(i => 18.0 + i * 0.6).ToArray();
    string[] seasons = ["2021-22", "2022-23", "2023-24", "2024-25", "2025-26"];
    foreach (var crop in new[] { "wheat", "maize", "beet", "rice" })
        await risk.AddReferenceAsync(scope, "seed", ReferenceKinds.Parcel, CohortLevels.CropNational, crop, seasons, scores);

    await risk.RecordFriAsync(scope, "seed", farm.Id, "2026-27",
        [new("crop_concentration", 1.2), new("spatial_concentration", 3.1), new("irrigation_diversity", 2.0), new("record_length", -5.8), new("worst_parcel_exposure", 1.2)]);
}
