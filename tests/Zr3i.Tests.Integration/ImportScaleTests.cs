using System.Diagnostics;
using Zr3i.Data;
using Zr3i.Kernel.Imports;
using Xunit;
using Xunit.Abstractions;

namespace Zr3i.Tests.Integration;

/// <summary>
/// Gate 1 (plan v2): 10,000 synthetic parcels import in a background job, errors are listed, and a re-import creates no duplicates.
/// Timings go to the test output; the time limit is a regression guard on this machine, not a throughput promise.
/// </summary>
[Collection(MongoCollection.Name)]
[Trait("Category", "Scale")]
public class ImportScaleTests(MongoFixture mongo, ITestOutputHelper output)
{
    private const int Parcels = 10_000;
    private static readonly TenantScope A = TenantScope.ForTenant("scale-tenant-a");

    [Fact]
    public async Task TenThousandSyntheticParcelsImportAndReimportCreatesNoDuplicates()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();

        var rows = Enumerable.Range(0, Parcels).Select(i => ImportCsv.Row(i)).ToList();
        for (var i = 99; i < Parcels; i += 1000) rows[i]["declared_area_feddan"] = "not a number";   // 10 known bad rows
        var csv = ImportCsv.Build(rows);
        output.WriteLine($"file: {csv.Length / 1_000_000.0:F1} MB, {Parcels} rows");

        var clock = Stopwatch.StartNew();
        var uploaded = await k.Imports.UploadAsync(A, "u1", "ten-thousand.csv", ImportCsv.Stream(csv));
        output.WriteLine($"upload (stored + job queued): {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await k.RunJobsAsync();
        var validated = (await k.Imports.GetAsync(A, uploaded.Id)).Batch;
        output.WriteLine($"validate job: {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(("validated", Parcels, Parcels - 10, 10), (validated.Status, validated.TotalRows, validated.ValidRows, validated.InvalidRows));

        await k.Imports.CommitAsync(A, "u1", uploaded.Id);
        clock.Restart();
        await k.RunJobsAsync();
        var committed = (await k.Imports.GetAsync(A, uploaded.Id)).Batch;
        output.WriteLine($"commit job: {clock.Elapsed.TotalSeconds:F1} s ({(Parcels - 10) / clock.Elapsed.TotalSeconds:F0} rows/s)");
        Assert.Equal(("committed", Parcels - 10, 0, 0, 0), (committed.Status, committed.Created, committed.Updated, committed.Unchanged, committed.Failed));

        Assert.Equal(Parcels - 10, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(Parcels / 5, await k.Store.Farms.CountAsync(A, _ => true));
        var report = await k.Imports.ErrorReportCsvAsync(A, uploaded.Id);
        Assert.Equal(10, report.Split("invalid_number").Length - 1);

        // Re-import the identical file.
        var boundaries = (await k.Store.Boundaries.FindAsync(A, _ => true, 1000)).Count;   // sample; the count below is exact
        var boundaryCount = await CountBoundariesAsync(k);
        clock.Restart();
        var again = await k.Imports.UploadAsync(A, "u1", "ten-thousand.csv", ImportCsv.Stream(csv));
        await k.RunJobsAsync();
        await k.Imports.CommitAsync(A, "u1", again.Id);
        await k.RunJobsAsync();
        var second = (await k.Imports.GetAsync(A, again.Id)).Batch;
        output.WriteLine($"re-import (upload + validate + commit): {clock.Elapsed.TotalSeconds:F1} s");

        Assert.Equal((0, 0, Parcels - 10, 0), (second.Created, second.Updated, second.Unchanged, second.Failed));
        Assert.Equal(Parcels - 10, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(Parcels / 5, await k.Store.Farms.CountAsync(A, _ => true));
        Assert.Equal(boundaryCount, await CountBoundariesAsync(k));
        Assert.True(boundaries > 0);
    }

    private static async Task<long> CountBoundariesAsync(KernelHarness k) =>
        await k.Database.GetCollection<MongoDB.Bson.BsonDocument>(Collections.Boundaries).CountDocumentsAsync(new MongoDB.Bson.BsonDocument("TenantId", A.TenantId));
}
