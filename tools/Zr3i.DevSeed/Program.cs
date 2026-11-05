using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Programmes;
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
string farmerOrgId = "";
string bankOrgId = "";
string insuranceOrgId = "";
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
    if (type.Key == "farmer") farmerOrgId = orgId;
    if (type.Key == "bank") bankOrgId = orgId;
    if (type.Key == "insurance") insuranceOrgId = orgId;

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
await SeedFarmerDataAsync(TenantScope.ForTenant(farmerOrgId), bankOrgId);
await SeedBankDataAsync(TenantScope.ForTenant(bankOrgId));
await SeedInsuranceDataAsync(TenantScope.ForTenant(insuranceOrgId));

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
        // One verdict of each kind for the Verification screens. The flagged parcel declares more than the boundary measures.
        var (declaredFactor, verdict, reason) = label switch
        {
            "ZR-KS-03" => (1.30, "FLAGGED", (string?)null),
            "ZR-KS-05" => (1.00, "PENDING", null),
            "ZR-KS-08" => (1.00, "NOT_ASSESSABLE", "insufficient_scenes"),
            "ZR-KS-09" => (1.00, "NOT_ASSESSABLE", "below_resolution"),
            _ => (1.00, "MEASURED", null),
        };
        var declared = Math.Round(area * declaredFactor, 1);
        var season = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, "2026-27", crop, declared, "2026-10-01", "contract"));
        var gap = Math.Round(100 * Math.Abs(area - declared) / declared, 1);
        await S<KernelStore>().Findings.InsertAsync(scope, new Finding
        {
            ParcelId = parcel.Id, ParcelSeasonId = season.Id, Season = "2026-27", Slot = FindingSlots.Area, Type = reason == null ? "area_gap" : "not_assessable", Status = verdict, Reason = reason,
            MeasuredFeddan = area, GapFeddan = area - declared, GapPercent = reason == null ? gap : null, NoisePercent = reason == null ? 5.3 : null, Direction = reason != null ? null : gap > 5.3 ? "OverDeclared" : "WithinNoise",
            DeclaredAreaFeddan = declared, DeclaredCrop = crop, BoundaryId = parcel.CurrentBoundaryId ?? string.Empty, BoundaryGeometrySha256 = new string('A', 64), MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, MethodState = "draft",
            ParametersSha256 = new string('B', 64), AccuracyRef = $"ZR3I-R0-2026-01/v1/area_gap/-/{(area < 40 ? "2-5" : "5-10")}", InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, CreatedBy = "seed",
        });

        if (label is "ZR-KS-03" or "ZR-KS-04")
        {
            // A season of Sentinel-2 passes: one lost to cloud.
            double[] ndvi = [0.12, 0.28, 0.55, 0.71, 0.74, 0.69];
            for (var i = 0; i < ndvi.Length; i++)
                await S<KernelStore>().ParcelObservations.InsertAsync(scope, new ParcelObservation
                {
                    ParcelId = parcel.Id, BoundaryId = parcel.CurrentBoundaryId ?? string.Empty, SceneId = $"S2B_36RTV_{20261015 + i * 25}", SensingTime = new DateTime(2026, 10, 15, 8, 50, 0, DateTimeKind.Utc).AddDays(i * 25),
                    FullPixels = 240, UsablePixels = i == 3 && label == "ZR-KS-03" ? 30 : 240, LostPixels = i == 3 && label == "ZR-KS-03" ? 210 : 0, LostShare = i == 3 && label == "ZR-KS-03" ? 0.875 : 0.0,
                    Readable = !(i == 3 && label == "ZR-KS-03"), NdviMedian = i == 3 && label == "ZR-KS-03" ? null : ndvi[i], NdviMean = ndvi[i], NdviMin = ndvi[i] - 0.1, NdviMax = ndvi[i] + 0.1, NdviPixels = 240,
                    PixelStackSha256 = new string('C', 64), SceneItemSha256 = new string('D', 64), PixelMethodId = "pixel-test", MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, MethodState = "draft",
                    Reader = "dev seed", Attribution = "Contains modified Copernicus Sentinel data 2026",
                });
        }

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

// Synthetic data for the Farmer organisation: its own farm with three parcels, a declared area each, and one area finding of each kind,
// plus a consent grant to the bank so "Who can see my data" has an entry. Findings are written directly because the worker is not part of seeding.
async Task SeedFarmerDataAsync(TenantScope scope, string granteeOrgId)
{
    var farms = S<FarmService>();
    var self = (await S<FarmerService>().ListAsync(scope)).Single();
    var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Hassan Farm", Governorate: "Kafr El Sheikh", FarmerId: self.Id));
    var captured = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    var store = S<KernelStore>();

    // (label, boundary side in metres or null, declared feddan, finding status, reason, gap percent)
    (string Label, double? Side, double Declared, string Status, string? Reason, double? Gap)[] parcels =
    [
        ("P-0021", 150, 10.0, "FLAGGED", null, 46.4),
        ("P-0022", 150, 5.4, "MEASURED", null, 0.8),
        ("P-0023", null, 3.0, "NOT_ASSESSABLE", "no_boundary", null),
        ("P-0024", 20, 0.1, "NOT_ASSESSABLE", "below_resolution", null),
    ];

    foreach (var (label, side, declared, status, reason, gap) in parcels)
    {
        var parcel = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
        if (side is { } sd)
        {
            var corners = new[] { (303800.0, 3409200.0), (303800 + sd, 3409200.0), (303800 + sd, 3409200 + sd), (303800.0, 3409200 + sd), (303800.0, 3409200.0) };
            var ring = corners.Select(c => { var (lon, lat) = Epsg32636.ToLonLat(c.Item1, c.Item2); return new LonLat(lon, lat); });
            await S<BoundaryService>().AddAsync(scope, "seed", parcel.Id, PolygonInput.FromExterior(ring), new BoundaryProvenance("survey", "dev seed", captured, 1));
        }

        var season = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, "2026-27", "wheat", declared, "2026-10-01", "contract"));
        await store.Findings.InsertAsync(scope, new Finding
        {
            ParcelId = parcel.Id, ParcelSeasonId = season.Id, Season = "2026-27", Slot = FindingSlots.Area, Type = reason == null ? "area_gap" : "not_assessable", Status = status, Reason = reason,
            GapPercent = gap, Direction = gap is null ? null : "OverDeclared", InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, CreatedBy = "seed",
        });
    }

    await S<ConsentService>().GrantAsync(scope, "seed", new GrantTerms(self.Id, granteeOrgId, "Verify input finance", DateTime.UtcNow.AddDays(90), null, null));
}

// A bank's own portfolio (imported by it, as the Account types board says) grouped into two tranches.
async Task SeedBankDataAsync(TenantScope scope)
{
    var farms = S<FarmService>();
    var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Delta Growers Programme", Governorate: "Kafr El Sheikh"));
    (string Ext, double Side, double Declared, string Status, string? Reason, double? Gap)[] rows =
    [
        ("B-01", 150, 5.4, "MEASURED", null, 0.8), ("B-02", 150, 7.5, "FLAGGED", null, 28.0), ("B-03", 100, 2.4, "MEASURED", null, 0.2),
        ("B-04", 100, 2.4, "PENDING", null, null), ("B-05", 20, 0.1, "NOT_ASSESSABLE", "below_resolution", null), ("B-06", 200, 9.5, "MEASURED", null, 0.1),
    ];
    foreach (var (ext, side, declared, status, reason, gap) in rows)
    {
        var parcel = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, ext.Replace("B-", "P-01"), ExternalId: ext));
        await S<BoundaryService>().AddAsync(scope, "seed", parcel.Id, SquareOf(side), new BoundaryProvenance("survey", "dev seed", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1));
        var season = await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, "2026-27", "wheat", declared, "2026-10-01", "contract"));
        await S<KernelStore>().Findings.InsertAsync(scope, new Finding
        {
            ParcelId = parcel.Id, ParcelSeasonId = season.Id, Season = "2026-27", Slot = FindingSlots.Area, Type = reason == null ? "area_gap" : "not_assessable", Status = status, Reason = reason,
            GapPercent = gap, NoisePercent = gap is null ? null : 5.3, Direction = gap is null ? null : gap > 5.3 ? "OverDeclared" : "WithinNoise", MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, MethodState = "draft",
            AccuracyRef = "ZR3I-R0-2026-01/v1/area_gap/-/2-5", InputSha256 = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, CreatedBy = "seed",
        });
    }

    var tranches = S<TrancheService>();
    await tranches.CreateAsync(scope, "seed", new TrancheRequest("TR-01", "2026-27", "2026-12-01", new ParcelSelection(null, null, ["B-01", "B-02", "B-03"])));
    await tranches.CreateAsync(scope, "seed", new TrancheRequest("TR-02", "2026-27", "2027-02-15", new ParcelSelection([farm.Id], null, null)));
}

// An insurer's own covered parcels, a season of scenes, and one event window (a frost) with scenes before and after it.
async Task SeedInsuranceDataAsync(TenantScope scope)
{
    var farms = S<FarmService>();
    var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("Covered Estate", Governorate: "Gharbia"));
    var store = S<KernelStore>();
    var ids = new List<string>();
    for (var i = 1; i <= 5; i++)
    {
        var parcel = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, $"C-0{i}", ExternalId: $"C-0{i}"));
        await S<BoundaryService>().AddAsync(scope, "seed", parcel.Id, SquareOf(120 + i * 10), new BoundaryProvenance("survey", "dev seed", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1));
        await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(parcel.Id, "2026-27", "wheat", 4.0 + i, "2026-10-01", "contract"));
        ids.Add(parcel.Id);

        // Scenes before (Jan 5) and after (Jan 17) the window, except: C-04 has no clear scene after, C-05 has none at all.
        async Task Scene(string id, DateTime when, double ndvi) => await store.ParcelObservations.InsertAsync(scope, new ParcelObservation
        {
            ParcelId = parcel.Id, BoundaryId = parcel.CurrentBoundaryId ?? string.Empty, SceneId = id, SensingTime = when, Readable = true, FullPixels = 240, UsablePixels = 240, NdviMedian = ndvi, NdviMean = ndvi,
            PixelStackSha256 = new string('C', 64), SceneItemSha256 = new string('D', 64), PixelMethodId = "pixel-test", MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, MethodState = "draft", Reader = "dev seed",
            Attribution = "Contains modified Copernicus Sentinel data 2027",
        });
        if (i <= 4) await Scene("S2B_36RTV_20270105", new DateTime(2027, 1, 5, 8, 50, 0, DateTimeKind.Utc), 0.70 - i * 0.01);
        if (i <= 3) await Scene("S2B_36RTV_20270117", new DateTime(2027, 1, 17, 8, 50, 0, DateTimeKind.Utc), i == 1 ? 0.52 : 0.68 - i * 0.01);
    }

    await S<EventWindowService>().CreateAsync(scope, "seed", new EventWindowRequest("Frost 10-14 Jan", "2026-27", "2027-01-10", "2027-01-14", new ParcelSelection([farm.Id], null, null)));
}

PolygonInput SquareOf(double side)
{
    var corners = new[] { (303800.0, 3409200.0), (303800 + side, 3409200.0), (303800 + side, 3409200 + side), (303800.0, 3409200 + side), (303800.0, 3409200.0) };
    return PolygonInput.FromExterior(corners.Select(c => { var (lon, lat) = Epsg32636.ToLonLat(c.Item1, c.Item2); return new LonLat(lon, lat); }));
}
