using MongoDB.Bson;
using MongoDB.Driver;
using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class BoundaryTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("boundary-tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("boundary-tenant-b");
    private static readonly DateTime Captured = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);   // before the harness clock (2026-11-05)

    private async Task<(KernelHarness K, Parcel Parcel)> SetupAsync(TenantScope? scope = null)
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        var farm = await k.Farms.CreateFarmAsync(scope ?? A, "u1", new NewFarm("Delta Agro Estate"));
        var parcel = await k.Farms.CreateParcelAsync(scope ?? A, "u1", new NewParcel(farm.Id, "ZR-KS-03"));
        return (k, parcel);
    }

    private static BoundaryProvenance Phone(DateTime? at = null) => new(BoundarySources.PhoneGps, "agronomist-7", at ?? Captured, 4.0);

    private static async Task<string[]> RejectedWith(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    [Fact]
    public async Task AValidBoundaryIsStoredWithItsAreaMethodAndProvenance()
    {
        var (k, parcel) = await SetupAsync();

        var b = await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SpikePolygon(), Phone());

        Assert.Equal(42009.0845, b.AreaM2, precision: 2);
        Assert.Equal(10.0002, b.AreaFeddan, precision: 4);
        Assert.Equal("GEO-AREA-2026-01", b.AreaMethodId);
        Assert.Equal(32636, b.AreaEpsg);
        Assert.Equal(("phone_gps", "agronomist-7", Captured, 4.0), (b.Source, b.CapturedBy, b.CapturedAt, b.AccuracyM));
        Assert.Null(b.Supersedes);
        Assert.Equal("boundary-tenant-a", b.TenantId);
        Assert.Equal("Polygon", b.Geometry.Type);
        Assert.Equal(5, b.Geometry.Coordinates[0].Count);
        Assert.InRange(b.CentroidLon, b.MinLon, b.MaxLon);

        Assert.Equal(b.Id, (await k.Store.Parcels.GetAsync(A, parcel.Id))!.CurrentBoundaryId);
        var audit = await k.Store.Audit.FindAsync(A, e => e.Action == "boundary.added");
        Assert.Equal(b.Id, audit.Single().ObjectId);
    }

    [Fact]
    public async Task ARecaptureSupersedesTheOldBoundaryWhichStaysReadable()
    {
        var (k, parcel) = await SetupAsync();
        var first = await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SpikePolygon(), Phone());
        var second = await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SquareMetres(150),
            new BoundaryProvenance(BoundarySources.Survey, "surveyor-2", Captured.AddDays(2), 0.5));

        Assert.Equal(first.Id, second.Supersedes);

        var current = await k.Boundaries.GetCurrentAsync(A, parcel.Id);
        Assert.Equal(second.Id, current!.Id);
        Assert.Equal(22500.0, current.AreaM2, precision: 1);
        Assert.Equal(600.0, current.PerimeterM, precision: 1);          // a 150 m square: the other input to the area-gap noise

        var history = await k.Boundaries.GetHistoryAsync(A, parcel.Id);
        Assert.Equal([first.Id, second.Id], history.Select(h => h.Id));
        Assert.Equal(42009.0845, history[0].AreaM2, precision: 2);   // unchanged
        Assert.Equal(2, await k.Store.Boundaries.FindAsync(A, b => b.ParcelId == parcel.Id).ContinueWith(t => t.Result.Count));
    }

    [Fact]
    public async Task TheCurrentBoundaryComesFromTheChainAndAgreesWithTheStoredPointer()
    {
        var (k, parcel) = await SetupAsync();
        for (var i = 0; i < 3; i++)
            await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SquareMetres(100 + i * 10), Phone(Captured.AddDays(i)));

        var history = await k.Boundaries.GetHistoryAsync(A, parcel.Id);
        var superseded = history.Where(h => h.Supersedes is not null).Select(h => h.Supersedes).ToHashSet();
        var tip = history.Single(h => !superseded.Contains(h.Id));

        Assert.Equal(tip.Id, (await k.Store.Parcels.GetAsync(A, parcel.Id))!.CurrentBoundaryId);
        Assert.Equal(tip.Id, (await k.Boundaries.GetCurrentAsync(A, parcel.Id))!.Id);
        Assert.Equal(history.OrderBy(h => h.CapturedAt).Select(h => h.Id), history.Select(h => h.Id));
    }

    [Fact]
    public async Task ACaptureOlderThanTheCurrentOneIsRefusedAndNothingChanges()
    {
        var (k, parcel) = await SetupAsync();
        var first = await k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SpikePolygon(), Phone());

        Assert.Equal(["capture_older_than_current"],
            await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, KernelHarness.SquareMetres(100), Phone(Captured.AddDays(-1)))));

        Assert.Single(await k.Boundaries.GetHistoryAsync(A, parcel.Id));
        Assert.Equal(first.Id, (await k.Store.Parcels.GetAsync(A, parcel.Id))!.CurrentBoundaryId);
    }

    [Fact]
    public async Task AnInvalidPolygonIsRefusedWithItsCodeAndStoresNothing()
    {
        var (k, parcel) = await SetupAsync();
        var bowTie = PolygonInput.FromExterior(new[] { (303800.0, 3409200.0), (303900, 3409300), (303900, 3409200), (303800, 3409300), (303800, 3409200) }
            .Select(p => { var (lon, lat) = Epsg32636.ToLonLat(p.Item1, p.Item2); return new LonLat(lon, lat); }));

        Assert.Equal(["self_intersection"], await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, bowTie, Phone())));

        Assert.Empty(await k.Boundaries.GetHistoryAsync(A, parcel.Id));
        Assert.Null((await k.Store.Parcels.GetAsync(A, parcel.Id))!.CurrentBoundaryId);
        Assert.Empty(await k.Store.Audit.FindAsync(A, e => e.Action == "boundary.added"));
    }

    [Fact]
    public async Task ProvenanceIsChecked()
    {
        var (k, parcel) = await SetupAsync();
        var poly = KernelHarness.SpikePolygon();
        var now = k.Clock.GetUtcNow().UtcDateTime;

        Assert.Equal(["invalid_source"], await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, poly, new("satellite_magic", "x", Captured, null))));
        Assert.Equal(["captured_by_required"], await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, poly, new(BoundarySources.Survey, " ", Captured, null))));
        Assert.Equal(["invalid_accuracy"], await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, poly, new(BoundarySources.Survey, "x", Captured, -1))));
        Assert.Equal(["capture_in_future"], await RejectedWith(() => k.Boundaries.AddAsync(A, "u1", parcel.Id, poly, new(BoundarySources.Survey, "x", now.AddHours(1), null))));
        Assert.Empty(await k.Boundaries.GetHistoryAsync(A, parcel.Id));
    }

    [Fact]
    public async Task ABoundaryCannotBeAddedToAnotherTenantsParcel()
    {
        var (k, parcelA) = await SetupAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => k.Boundaries.AddAsync(B, "u1", parcelA.Id, KernelHarness.SpikePolygon(), Phone()));

        Assert.Empty(await k.Store.Boundaries.FindAsync(B, _ => true));
        Assert.Empty(await k.Store.Boundaries.FindAsync(A, _ => true));
        Assert.Empty(await k.Boundaries.GetHistoryAsync(B, parcelA.Id));
        Assert.Null(await k.Boundaries.GetCurrentAsync(B, parcelA.Id));
    }

    [Fact]
    public async Task TheStoredPolygonIsCounterClockwiseAndUsableByTheGeoIndex()
    {
        var (k, parcel) = await SetupAsync();
        var clockwise = PolygonInput.FromExterior(KernelHarness.SquareMetres(200).Rings[0].Reverse());
        var b = await k.Boundaries.AddAsync(A, "u1", parcel.Id, clockwise, Phone());

        var ring = b.Geometry.Coordinates[0].Select(p => new LonLat(p[0], p[1])).ToList();
        Assert.True(BoundaryValidator.SignedArea(ring) > 0);

        // A point-in-polygon query through the 2dsphere index, scoped to the tenant, finds the boundary.
        var raw = k.Database.GetCollection<BsonDocument>(Collections.Boundaries);
        var inside = new BsonDocument("type", "Point") { { "coordinates", new BsonArray { b.CentroidLon, b.CentroidLat } } };
        var filter = new BsonDocument { { "TenantId", A.TenantId }, { "Geometry", new BsonDocument("$geoIntersects", new BsonDocument("$geometry", inside)) } };
        var hits = await raw.Find(filter).ToListAsync();
        Assert.Equal(b.Id, hits.Single()["_id"].AsString);

        var elsewhere = new BsonDocument("type", "Point") { { "coordinates", new BsonArray { 31.5, 30.5 } } };
        filter["Geometry"] = new BsonDocument("$geoIntersects", new BsonDocument("$geometry", elsewhere));
        Assert.Empty(await raw.Find(filter).ToListAsync());

        var indexes = await (await raw.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("Geometry") && i["key"]["Geometry"] == "2dsphere");
    }
}
