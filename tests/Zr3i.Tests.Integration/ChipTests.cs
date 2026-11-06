using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Imagery;
using Zr3i.Kernel;
using Zr3i.Kernel.Chips;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Identity;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>Pictures for a parcel and a scene: queued by the web app, drawn by the worker, stored as files with a row for each, read back only by their own organisation.</summary>
[Collection(MongoCollection.Name)]
public class ChipTests
{
    private const string SceneId = "S2C_36RTV_20261027_0_L2A";
    private static readonly DateTime Captured = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly RasterGrid TenGrid = new(199980, 3500040, 10);

    /// <summary>A raster on tile 36RTV's real geometry: vegetation-like numbers, and every fifth 20 m class cell is cloud.</summary>
    private sealed class FakeBandReader : IBandReader
    {
        public string Name => "fake 1.0";

        public Task<BandWindow> ReadAsync(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct = default)
        {
            var grid = href == "scl" ? new RasterGrid(199980, 3500040, 20) : TenGrid;
            var window = grid.WindowOf(latticePixels);
            var values = new int[window.Cells];
            for (var r = 0; r < window.Height; r++)
                for (var c = 0; c < window.Width; c++)
                {
                    int col = window.XOff + c, row = window.YOff + r;
                    values[r * window.Width + c] = href switch
                    {
                        "scl" => col % 5 == 0 ? 9 : 4,
                        "nir" => 3500 + (col * 7 + row * 3) % 400,
                        "red" => 900 + (col * 3 + row * 5) % 300,
                        "green" => 800 + (col + row) % 200,
                        _ => 500 + (col + row * 2) % 150,
                    };
                }

            return Task.FromResult(new BandWindow(values, window, grid));
        }
    }

    private static Scene Scene() => new()
    {
        SceneId = SceneId, Source = "fake", Collection = "sentinel-2-l2a", Platform = "sentinel-2c", SensingTime = new DateTime(2026, 10, 27, 8, 51, 0, DateTimeKind.Utc),
        Tile = "36RTV", Epsg = 32636, CloudCover = 1, ProcessingBaseline = "05.13", BoaOffsetApplied = true, ItemSha256 = new string('C', 64),
        Attribution = "Contains modified Copernicus Sentinel data 2026", MinLon = 30.0, MinLat = 30.5, MaxLon = 31.2, MaxLat = 31.5,
        Assets = new() { ["blue"] = "blue", ["green"] = "green", ["red"] = "red", ["nir"] = "nir", ["scl"] = "scl" },
    };

    private sealed record Org(string Id, string Token, string UserId);

    private static async Task<Org> OrgAsync(WebHarness web, string email, string name, string role = "org-admin", string? orgId = null)
    {
        string id, invite;
        if (orgId is null) (id, invite) = await web.OrgWithInvitationAsync(email, role, name);
        else { id = orgId; invite = await web.Service<InvitationService>().InviteAsync(TenantScope.ForTenant(orgId), "system", email, [role]); }
        var token = await web.SignInAsync(email, await web.AcceptAsync(invite));
        var me = await (await web.Http.SendAsync(web.Request(HttpMethod.Get, "/api/me", token))).Content.ReadFromJsonAsync<JsonElement>();
        return new Org(id, token, me.GetProperty("id").GetString()!);
    }

    private static async Task<WebHarness> StartAsync(string root) =>
        await WebHarness.StartAsync(extraSettings: new Dictionary<string, string?> { ["Imagery:ChipRoot"] = root });

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "zr3i-chip-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>A parcel with a current boundary. When <paramref name="observe"/> is set, the scene is in the catalogue and the parcel has a readable observation of it.</summary>
    private static async Task<Parcel> ParcelAsync(WebHarness web, string orgId, string label, double side = 150, bool observe = true, string reason = "")
    {
        var scope = TenantScope.ForTenant(orgId);
        var farm = await web.Service<FarmService>().CreateFarmAsync(scope, "seed", new NewFarm("F " + label));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(scope, "seed", new NewParcel(farm.Id, label));
        await web.Service<BoundaryService>().AddAsync(scope, "seed", parcel.Id, KernelHarness.SquareMetres(side, 303800, 3409200), new BoundaryProvenance("survey", "s", Captured, 1));
        var current = (await web.Service<BoundaryService>().GetCurrentAsync(scope, parcel.Id))!;
        if (observe)
        {
            await web.Service<SceneCatalog>().UpsertAsync([Scene()]);
            await web.Service<KernelStore>().ParcelObservations.InsertAsync(scope, new ParcelObservation
            {
                ParcelId = parcel.Id, BoundaryId = current.Id, SceneId = SceneId, SensingTime = Scene().SensingTime, FullPixels = 200, UsablePixels = 160, Readable = reason == "",
                Reason = reason == "" ? null : reason, PixelStackSha256 = "ABCDEF0123", PixelMethodId = "GEO-PIXEL-2026-01", IngestedAt = DateTime.UtcNow,
            });
        }

        return parcel;
    }

    private static ChipHandler Handler(WebHarness web) => new(web.Service<KernelStore>(), new FakeBandReader(), web.Service<IChipStore>(), TimeProvider.System);

    private static async Task<JobOutcome> RunNewestChipJobAsync(WebHarness web, string orgId)
    {
        var scope = TenantScope.ForTenant(orgId);
        var data = web.Service<DataStore>();
        var job = (await data.Jobs.ListAsync(scope, ChipPlanner.JobType)).Last();
        return await Handler(web).HandleAsync(new JobContext(job, data.CreateJobClaimer(), "test"), CancellationToken.None);
    }

    private static Task<HttpResponseMessage> Post(WebHarness web, string url, string token, object body) => web.Http.SendAsync(web.Request(HttpMethod.Post, url, token, body));
    private static Task<HttpResponseMessage> Get(WebHarness web, string url, string token) => web.Http.SendAsync(web.Request(HttpMethod.Get, url, token));

    // ---- making them ----

    [Fact]
    public async Task AskingQueuesAJobAndTheWorkerMakesBothPicturesWithTheirProvenance()
    {
        var root = TempRoot();
        await using var web = await StartAsync(root);
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1");

        var queued = await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.True((await RunNewestChipJobAsync(web, org.Id)).Succeeded);

        var list = await (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["ndvi", "true_colour"], list.EnumerateArray().Select(c => c.GetProperty("kind").GetString()!).Order(StringComparer.Ordinal).ToArray());
        foreach (var chip in list.EnumerateArray())
        {
            var image = await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips/{chip.GetProperty("id").GetString()}/image", org.Token);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
            var bytes = await image.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
            Assert.Equal(chip.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
            Assert.Equal(chip.GetProperty("sha256").GetString(), image.Headers.GetValues("X-Content-SHA256").Single());
            Assert.Equal("fake 1.0", chip.GetProperty("reader").GetString());
            Assert.Equal("Contains modified Copernicus Sentinel data 2026", chip.GetProperty("attribution").GetString());
            Assert.Equal(new string('C', 64), chip.GetProperty("sceneItemSha256").GetString());
            Assert.Equal("ABCDEF0123", chip.GetProperty("pixelStackSha256").GetString());
            Assert.True(chip.GetProperty("scale").GetInt32() >= 2);
        }

        var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.Contains(org.Id, f));
    }

    [Fact]
    public async Task RunningTheSameJobTwiceAddsNothingAndAskingAgainSaysTheyAreReady()
    {
        var root = TempRoot();
        await using var web = await StartAsync(root);
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1");
        await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, org.Id);

        Assert.True((await RunNewestChipJobAsync(web, org.Id)).Succeeded);
        var again = await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("ready", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(2, (await web.Service<KernelStore>().ParcelChips.FindAsync(TenantScope.ForTenant(org.Id), _ => true)).Count);
        Assert.Equal(2, Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).Length);
        Assert.Single(await web.Service<DataStore>().Jobs.ListAsync(TenantScope.ForTenant(org.Id), ChipPlanner.JobType));
    }

    [Fact]
    public async Task AParcelTooSmallToTestGetsNoPictureAndTheReasonIsBelowResolutionNeverNoCrop()
    {
        await using var web = await StartAsync(TempRoot());
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1", reason: ObservationReasons.BelowResolution);

        var response = await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("below_resolution", body);
        Assert.DoesNotContain("no_crop", body);
        Assert.Empty(await web.Service<DataStore>().Jobs.ListAsync(TenantScope.ForTenant(org.Id), ChipPlanner.JobType));
    }

    [Fact]
    public async Task ASceneThatLostTheParcelOrWasNeverReadGetsNoPicture()
    {
        await using var web = await StartAsync(TempRoot());
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var lost = await ParcelAsync(web, org.Id, "P-LOST", reason: ObservationReasons.SceneLost);
        var unread = await ParcelAsync(web, org.Id, "P-NEW", observe: false);

        Assert.Contains("not_readable", await (await Post(web, $"/api/organisations/{org.Id}/parcels/{lost.Id}/chips", org.Token, new { sceneId = SceneId })).Content.ReadAsStringAsync());
        Assert.Contains("not_observed", await (await Post(web, $"/api/organisations/{org.Id}/parcels/{unread.Id}/chips", org.Token, new { sceneId = SceneId })).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Post(web, $"/api/organisations/{org.Id}/parcels/{lost.Id}/chips", org.Token, new { sceneId = "S2X_UNKNOWN" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(web, $"/api/organisations/{org.Id}/parcels/{lost.Id}/chips", org.Token, new { })).StatusCode);
    }

    // ---- who may ----

    [Fact]
    public async Task AViewerCanLookButNotAskAndAnotherOrganisationCanDoNeither()
    {
        var root = TempRoot();
        await using var web = await StartAsync(root);
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var viewer = await OrgAsync(web, "viewer@delta.example", "x", "viewer", org.Id);
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        var parcel = await ParcelAsync(web, org.Id, "P-1");
        await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, org.Id);
        var chipId = (await (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token)).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", viewer.Token, new { sceneId = SceneId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips/{chipId}/image", viewer.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips/{chipId}/image", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{other.Id}/parcels/{parcel.Id}/chips", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{other.Id}/parcels/{parcel.Id}/chips/{chipId}/image", other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(web, $"/api/organisations/{other.Id}/parcels/{parcel.Id}/chips", other.Token, new { sceneId = SceneId })).StatusCode);
    }

    [Fact]
    public async Task AChipCannotBeReadThroughAnotherParcelOfTheSameOrganisation()
    {
        await using var web = await StartAsync(TempRoot());
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var a = await ParcelAsync(web, org.Id, "P-A");
        var b = await ParcelAsync(web, org.Id, "P-B");
        await Post(web, $"/api/organisations/{org.Id}/parcels/{a.Id}/chips", org.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, org.Id);
        var chipId = (await (await Get(web, $"/api/organisations/{org.Id}/parcels/{a.Id}/chips", org.Token)).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, $"/api/organisations/{org.Id}/parcels/{b.Id}/chips/{chipId}/image", org.Token)).StatusCode);
    }

    // ---- the files ----

    [Fact]
    public async Task AFileThatNoLongerMatchesItsHashOrIsMissingIsRefusedNotServed()
    {
        var root = TempRoot();
        await using var web = await StartAsync(root);
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1");
        await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, org.Id);
        var chips = (await (await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token)).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);

        await File.WriteAllBytesAsync(files[0], [1, 2, 3]);
        File.Delete(files[1]);

        foreach (var chip in chips)
        {
            var response = await Get(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips/{chip.GetProperty("id").GetString()}/image", org.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("chip_corrupt", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task IdsAreCleanedIntoThePathAndAPathOutsideTheRootIsRefused()
    {
        var root = TempRoot();
        var store = new FileChipStore(root);

        var path = await store.WriteAsync("../../evil", "a/b", "S2..\\x", "true_colour", [1, 2, 3], "ABCDEF0123456789");

        Assert.DoesNotContain("..", path);
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, path)));
        Assert.Equal([1, 2, 3], await store.ReadAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("../outside.png"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("a/../../outside.png"));
        Assert.Null(await store.ReadAsync("nothing/here.png"));
    }

    [Fact]
    public async Task TheSameSceneAndBoundaryGiveTheSameBytes()
    {
        var root = TempRoot();
        await using var web = await StartAsync(root);
        var org = await OrgAsync(web, "admin@delta.example", "Delta Agro");
        var parcel = await ParcelAsync(web, org.Id, "P-1");
        await Post(web, $"/api/organisations/{org.Id}/parcels/{parcel.Id}/chips", org.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, org.Id);
        var first = (await web.Service<KernelStore>().ParcelChips.FindAsync(TenantScope.ForTenant(org.Id), _ => true)).ToDictionary(c => c.Kind, c => c.Sha256);

        // Make them again from nothing: a second organisation with an identical parcel at the same place.
        var other = await OrgAsync(web, "admin@other.example", "Other Co");
        var twin = await ParcelAsync(web, other.Id, "P-1");
        await Post(web, $"/api/organisations/{other.Id}/parcels/{twin.Id}/chips", other.Token, new { sceneId = SceneId });
        await RunNewestChipJobAsync(web, other.Id);
        var second = (await web.Service<KernelStore>().ParcelChips.FindAsync(TenantScope.ForTenant(other.Id), _ => true)).ToDictionary(c => c.Kind, c => c.Sha256);

        Assert.Equal(first, second);
    }
}
