using MongoDB.Driver;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel.Farms;
using Zr3i.Modules.Verification;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ReadabilityTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("readability-a");
    private static readonly TenantScope B = TenantScope.ForTenant("readability-b");
    private static readonly DateTime Captured = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static async Task<Parcel> ParcelWithSquareAsync(KernelHarness k, TenantScope scope, double side, string label)
    {
        var farm = await k.Farms.CreateFarmAsync(scope, "u1", new NewFarm("Farm " + label));
        var parcel = await k.Farms.CreateParcelAsync(scope, "u1", new NewParcel(farm.Id, label));
        await k.Boundaries.AddAsync(scope, "u1", parcel.Id, KernelHarness.SquareMetres(side), new BoundaryProvenance("survey", "surveyor-1", Captured, 0.5));
        return parcel;
    }

    [Fact]
    public async Task AnAddedBoundaryKeepsItsWholePixelCountAndTheMethodThatMadeIt()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        var farm = await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("Delta Agro Estate"));
        var parcel = await k.Farms.CreateParcelAsync(A, "u1", new NewParcel(farm.Id, "ZR-SPIKE-01"));

        var boundary = await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SpikePolygon(), new BoundaryProvenance("survey", "surveyor-1", Captured, 0.5));

        Assert.Equal((337, "GEO-PIXEL-2026-01"), (boundary.FullPixels, boundary.PixelMethodId));
        Assert.Equal(337, (await k.Boundaries.GetCurrentAsync(A, parcel.Id))!.FullPixels);
    }

    [Fact]
    public async Task ReadableShareIsReportedPerSizeClassForTheTenantsCurrentBoundaries()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        // Squares aligned to the 10 m lattice, so the whole-pixel counts are worked out by hand: 30 m -> 1 pixel, 60 m -> 16, 100 m -> 64, 150 m -> 169ish, 200 m -> 324ish, 300 m -> 784ish.
        foreach (var (side, label) in new[] { (30.0, "tiny"), (60.0, "small"), (100.0, "medium"), (150.0, "large"), (200.0, "larger"), (300.0, "huge") })
            await ParcelWithSquareAsync(k, A, side, label);
        var farm = await k.Farms.CreateFarmAsync(A, "u1", new NewFarm("No boundary yet"));
        await k.Farms.CreateParcelAsync(A, "u1", new NewParcel(farm.Id, "pending"));
        await ParcelWithSquareAsync(k, B, 30, "other tenant's tiny parcel");      // must not appear in A's report

        var report = await new ReadabilityService(k.Store).ReportAsync(A, new SizeClassScheme([1, 3, 10]));

        // feddan: 30 m = 0.21, 60 m = 0.86, 100 m = 2.38, 150 m = 5.36, 200 m = 9.52, 300 m = 21.4
        Assert.Equal(["0-1", "1-3", "3-10", "10+"], report.Classes.Select(c => c.Key));
        Assert.Equal([(2, 1), (1, 1), (2, 2), (1, 1)], report.Classes.Select(c => (c.Parcels, c.Readable)));
        Assert.Equal([0.5, 1.0, 1.0, 1.0], report.Classes.Select(c => c.Share!.Value));
        Assert.Equal(1, report.ParcelsWithoutBoundary);
        Assert.Equal(("GEO-PIXEL-2026-01", 5.0, 8), (report.MethodId, report.BufferMetres, report.MinFullPixels));
    }

    [Fact]
    public async Task OnlyTheCurrentBoundaryCountsWhenAParcelHasBeenRecaptured()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        var parcel = await ParcelWithSquareAsync(k, A, 30, "grew");        // 0.21 feddan, one whole pixel: not readable
        await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SquareMetres(200), new BoundaryProvenance("survey", "surveyor-2", Captured.AddDays(1), 0.5));

        var report = await new ReadabilityService(k.Store).ReportAsync(A, new SizeClassScheme([1, 3, 10]));

        Assert.Equal(1, report.Classes.Sum(c => c.Parcels));
        Assert.Equal((0, 1, 1), (report.Classes[0].Parcels, report.Classes[2].Parcels, report.Classes[2].Readable));   // 200 m = 9.52 feddan: it now sits in "3-10", readable
    }

    [Fact]
    public async Task AnEmptyBookGivesEmptyClassesNotZeroShares()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();

        var report = await new ReadabilityService(k.Store).ReportAsync(A, new SizeClassScheme([1, 3]));

        Assert.All(report.Classes, c => Assert.Null(c.Share));
        Assert.Equal(0, report.ParcelsWithoutBoundary);
    }

    [Fact]
    public async Task ABoundaryStoredBeforeTheCountWasKeptIsMeasuredFromItsShape()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        var parcel = await ParcelWithSquareAsync(k, A, 100, "legacy");
        var boundary = (await k.Boundaries.GetCurrentAsync(A, parcel.Id))!;
        // Simulate an older row: no count and no method id recorded.
        await k.Database.GetCollection<MongoDB.Bson.BsonDocument>(Collections.Boundaries).UpdateOneAsync(
            new MongoDB.Bson.BsonDocument("_id", boundary.Id), MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Set("FullPixels", 0).Set("PixelMethodId", ""));

        var report = await new ReadabilityService(k.Store).ReportAsync(A, new SizeClassScheme([1, 3]));

        Assert.Equal((1, 1), (report.Classes[1].Parcels, report.Classes[1].Readable));       // 100 m square = 2.38 feddan, 64 whole pixels
    }

    [Fact]
    public async Task TheEndpointNeedsConfiguredSizeClassesAndPermission()
    {
        await using var unconfigured = await WebHarness.StartAsync();
        var (orgId, invite) = await unconfigured.OrgWithInvitationAsync("admin@delta.example");
        var token = await unconfigured.SignInAsync("admin@delta.example", await unconfigured.AcceptAsync(invite));
        var refused = await unconfigured.Http.SendAsync(unconfigured.Request(HttpMethod.Get, $"/api/organisations/{orgId}/readability", token));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("size_classes_not_configured", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await using var web = await WebHarness.StartAsync(extraSettings: new Dictionary<string, string?>
        {
            ["Verification:SizeClassEdgesFeddan:0"] = "1", ["Verification:SizeClassEdgesFeddan:1"] = "3", ["Verification:SizeClassEdgesFeddan:2"] = "10",
        });
        var (org, inv) = await web.OrgWithInvitationAsync("admin@delta.example");
        var (other, inv2) = await web.OrgWithInvitationAsync("admin@other.example", orgName: "Other");
        var admin = await web.SignInAsync("admin@delta.example", await web.AcceptAsync(inv));
        var outsider = await web.SignInAsync("admin@other.example", await web.AcceptAsync(inv2));
        var farm = await web.Service<FarmService>().CreateFarmAsync(TenantScope.ForTenant(org), "seed", new NewFarm("F"));
        var parcel = await web.Service<FarmService>().CreateParcelAsync(TenantScope.ForTenant(org), "seed", new NewParcel(farm.Id, "P"));
        await web.Service<BoundaryService>().AddAsync(TenantScope.ForTenant(org), "seed", parcel.Id, KernelHarness.SquareMetres(100), new BoundaryProvenance("survey", "s", Captured, 1));

        var ok = await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{org}/readability", admin));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["0-1", "1-3", "3-10", "10+"], body.GetProperty("classes").EnumerateArray().Select(c => c.GetString("key")));
        var mid = body.GetProperty("classes")[1];
        Assert.Equal((1, 1, 1.0), (mid.GetProperty("parcels").GetInt32(), mid.GetProperty("readable").GetInt32(), mid.GetProperty("share").GetDouble()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("classes")[0].GetProperty("share").ValueKind);        // empty class: no figure
        Assert.False(body.TryGetProperty("overall", out _));                                                     // never pooled
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(web.Request(HttpMethod.Get, $"/api/organisations/{org}/readability", outsider))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.GetAsync($"/api/organisations/{org}/readability")).StatusCode);
        _ = other;
    }
}

internal static class JsonExtensions
{
    public static string? GetString(this JsonElement e, string name) => e.GetProperty(name).GetString();
}
