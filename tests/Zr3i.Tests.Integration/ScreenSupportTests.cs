using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Findings;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Read APIs the farmer, company and analyst screens need: the parcels list and the organisation names on consent grants.</summary>
[Collection(MongoCollection.Name)]
public class ScreenSupportTests
{
    private const string Season = "2026-27";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private sealed record Party(string OrgId, string Token);

    private static async Task<Party> PartyAsync(WebHarness web, string email, string orgName, string type = "company")
    {
        var (orgId, invite) = await web.OrgWithInvitationAsync(email, "org-admin", orgName, type);
        return new Party(orgId, await web.SignInAsync(email, await web.AcceptAsync(invite)));
    }

    [Fact]
    public async Task ConsentGrantsNameBothOrganisationsSoAFarmerCanSeeWhoCanReadTheirData()
    {
        await using var web = await WebHarness.StartAsync();
        var farmer = await PartyAsync(web, "ali@farm.example", "Abu Ali Farm", "farmer");
        var bank = await PartyAsync(web, "admin@nile-bank.example", "Nile Bank", "bank");
        var self = (await web.Service<FarmerService>().ListAsync(TenantScope.ForTenant(farmer.OrgId))).Single();
        var created = await web.Http.SendAsync(web.Request(HttpMethod.Post, $"/api/organisations/{farmer.OrgId}/consents", farmer.Token,
            new { farmerId = self.Id, granteeTenantId = bank.OrgId, purpose = "Verify input finance", validUntil = DateTime.UtcNow.AddDays(90) }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var mine = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{farmer.OrgId}/consents", farmer.Token)))).EnumerateArray().Single();
        Assert.Equal(("Abu Ali Farm", "Nile Bank", "owner"), (mine.GetProperty("ownerName").GetString(), mine.GetProperty("granteeName").GetString(), mine.GetProperty("role").GetString()));

        var theirs = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{bank.OrgId}/consents", bank.Token)))).EnumerateArray().Single();
        Assert.Equal(("grantee", "Abu Ali Farm"), (theirs.GetProperty("role").GetString(), theirs.GetProperty("ownerName").GetString()));
    }

    [Fact]
    public async Task TheParcelsListShowsDeclaredAndMeasuredAreaAndTheCurrentAreaFindingForASeasonWithoutRanking()
    {
        await using var web = await WebHarness.StartAsync();
        var org = await PartyAsync(web, "admin@delta.example", "Delta Agro");
        var outsider = await PartyAsync(web, "admin@other.example", "Other");
        var scope = TenantScope.ForTenant(org.OrgId);
        var farms = web.Service<FarmService>();
        var farm = await farms.CreateFarmAsync(scope, "seed", new NewFarm("F-0404"));

        async Task<(Parcel, ParcelSeason)> Make(string label, double? side, double declared)
        {
            var p = await farms.CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
            if (side is not null) await web.Service<BoundaryService>().AddAsync(scope, "seed", p.Id, KernelHarness.SquareMetres(side.Value), new BoundaryProvenance("survey", "s", Captured, 1));
            return (p, await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(p.Id, Season, "wheat", declared, "2026-10-01", "contract")));
        }

        var (flagged, flaggedSeason) = await Make("P-0001", 150, 10.0);
        var (matches, matchesSeason) = await Make("P-0002", 150, 5.4);
        var (bare, _) = await Make("P-0003", null, 3.0);

        async Task Finding(Parcel p, ParcelSeason ps, string status, double gapPercent, string? supersedes = null, int hoursAhead = 0) =>
            await web.Service<KernelStore>().Findings.InsertAsync(scope, new Finding
            {
                ParcelId = p.Id, ParcelSeasonId = ps.Id, Season = Season, Slot = FindingSlots.Area, Type = "area_gap", Status = status, GapPercent = gapPercent,
                Direction = "OverDeclared", InputSha256 = Guid.NewGuid().ToString("N"), Supersedes = supersedes, CreatedAt = DateTime.UtcNow.AddHours(hoursAhead),
            });
        await Finding(flagged, flaggedSeason, "MEASURED", 1.0, hoursAhead: 1);       // replaced by the next one, although its timestamp is later (clock skew)
        var older = (await web.Service<KernelStore>().Findings.FindAsync(scope, f => f.ParcelId == flagged.Id)).Single();
        await Finding(flagged, flaggedSeason, "FLAGGED", 46.4, supersedes: older.Id);
        await Finding(matches, matchesSeason, "MEASURED", 0.2);

        // A declaration for another season must not leak into this season's row.
        await farms.DeclareSeasonAsync(scope, "seed", new NewParcelSeason(matches.Id, "2025-26", "maize", 99.0, "2025-10-01", "contract"));

        var url = $"/api/organisations/{org.OrgId}/parcels?season={Season}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url, outsider.Token))).StatusCode);

        var page = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url, org.Token)));
        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!);

        Assert.Equal(["P-0001", "P-0002", "P-0003"], rows.Keys.Order());
        var a = rows["P-0001"];
        Assert.Equal(("F-0404", "wheat", 10.0, "FLAGGED"), (a.GetProperty("farmName").GetString(), a.GetProperty("crop").GetString(), a.GetProperty("declaredAreaFeddan").GetDouble(), a.GetProperty("finding").GetProperty("status").GetString()));
        Assert.Equal(150.0 * 150 / 4200.83, a.GetProperty("measuredAreaFeddan").GetDouble(), 2);
        Assert.Equal(46.4, a.GetProperty("finding").GetProperty("gapPercent").GetDouble());           // the current finding, not the replaced one
        Assert.Equal("MEASURED", rows["P-0002"].GetProperty("finding").GetProperty("status").GetString());
        Assert.Equal((5.4, "wheat"), (rows["P-0002"].GetProperty("declaredAreaFeddan").GetDouble(), rows["P-0002"].GetProperty("crop").GetString()));   // this season's declaration, not 2025-26's
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (rows["P-0003"].GetProperty("measuredAreaFeddan").ValueKind, rows["P-0003"].GetProperty("finding").ValueKind));
        Assert.Equal(3.0, rows["P-0003"].GetProperty("declaredAreaFeddan").GetDouble());

        // Without a season there is no declaration and no finding, only what the parcel is.
        var plain = (await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{org.OrgId}/parcels", org.Token)))).GetProperty("items").EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("declaredAreaFeddan").ValueKind);

        // Paged, other organisations' parcels never appear, and a bad page size is refused.
        var first = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "&pageSize=2", org.Token)));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("nextCursor").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Request(HttpMethod.Get, url + "&pageSize=5000", org.Token))).StatusCode);
        var other = await Json(await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{outsider.OrgId}/parcels?season={Season}", outsider.Token)));
        Assert.Equal(0, other.GetProperty("items").GetArrayLength());
    }
}
