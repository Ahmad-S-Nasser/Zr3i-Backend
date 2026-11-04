using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Findings;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AccuracyRegisterTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("acc-a");
    private static readonly TenantScope B = TenantScope.ForTenant("acc-b");

    private static Finding F(string aref, string type, string status, string? supersedes = null) => new()
    {
        ParcelId = "p", ParcelSeasonId = "s", Season = "2026-27", Slot = type == "area_gap" ? "area" : "crop", Type = type, Status = status,
        MethodId = "ZR3I-R0-2026-01", MethodVersion = 1, AccuracyRef = aref, Supersedes = supersedes, InputSha256 = Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public async Task EachStratumCountsOnlyCurrentFindingsAndAccuracyIsNeverStatedWithoutChecks()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        const string beet = "ZR3I-R0-2026-01/v1/crop_present/beet/5-10";
        var old = F(beet, "crop_present", "PENDING");
        await k.Store.Findings.InsertAsync(A, old);
        await k.Store.Findings.InsertAsync(A, F(beet, "crop_present", "MEASURED", supersedes: old.Id));
        await k.Store.Findings.InsertAsync(A, F(beet, "crop_present", "MEASURED"));
        await k.Store.Findings.InsertAsync(A, F("ZR3I-R0-2026-01/v1/area_gap/-/unclassified", "area_gap", "FLAGGED"));
        await k.Store.Findings.InsertAsync(B, F(beet, "crop_present", "MEASURED"));

        var rows = await new AccuracyRegister(k.Store).ListAsync(A);

        Assert.Equal(2, rows.Count);
        var crop = rows.Single(r => r.AccuracyRef == beet);
        Assert.Equal((2, "beet", "5-10", "crop_present"), (crop.CurrentFindings, crop.Crop, crop.SizeClass, crop.FindingType));
        Assert.Equal(2, crop.ByStatus["MEASURED"]);
        Assert.False(crop.ByStatus.ContainsKey("PENDING"));
        Assert.All(rows, r => Assert.Equal((AccuracyRegister.NotEstablished, null), (r.AccuracyState, r.AccuracyValue)));
        Assert.Equal(1, (await new AccuracyRegister(k.Store).ListAsync(B)).Single().CurrentFindings);   // other tenant counted separately
    }
}
