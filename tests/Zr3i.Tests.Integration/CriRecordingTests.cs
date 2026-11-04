using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class CriRecordingTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("cri-a");

    private static readonly CriFeatures Ks03 = new(24.0, 0.58, 46.0, 12.0, 34.0, [73.5, 79, 97, 100, 100, 104, 108, 112], 0.9);

    private async Task<(KernelHarness, CriService)> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return (k, new CriService(new IndicatorRecorder(k.Store, k.Clock)));
    }

    [Fact]
    public async Task TheWorkedExampleIsRecordedAsAFrozenSpecifiedResultAndRecomputingAddsNothing()
    {
        var (k, svc) = await NewAsync();
        var first = await svc.RecordAsync(A, "u", "ZR-KS-03", Ks03);
        var again = await svc.RecordAsync(A, "u", "ZR-KS-03", Ks03);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(50.3, Math.Round(first.Value, 1));
        Assert.Equal(("CRI", "FRI-2026-01", 1, "specified", false, 0.9), (first.Indicator, first.MethodId, first.MethodVersion, first.Maturity, first.Insufficient, first.ValidFraction));
        Assert.Equal(7, first.Components.Count);
        Assert.Empty(first.MissingComponents);
        Assert.Single(await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "ZR-KS-03"));
    }

    [Fact]
    public async Task ANewParcelWithoutYieldHistoryKeepsTheYieldWeightAsMissingAndIsRenormalised()
    {
        var (_, svc) = await NewAsync();
        var r = await svc.RecordAsync(A, "u", "NEW-1", Ks03 with { ClosedSeasonYields = [100] });

        Assert.Equal(["yield_stability"], r.MissingComponents);
        var yield = r.Components.Single(c => c.Key == "yield_stability");
        Assert.Equal((15.0, null, false), (yield.Weight, yield.Score, yield.Established));
        Assert.Equal(r.Components.Where(c => c.Established).Sum(c => c.Score!.Value * c.Weight) / 85.0, r.Value, 9);
    }

    [Fact]
    public async Task ALowValidFractionIsStoredAsInsufficientAndChangesTheHashSoItIsANewResult()
    {
        var (_, svc) = await NewAsync();
        var ok = await svc.RecordAsync(A, "u", "GATE-1", Ks03);
        var low = await svc.RecordAsync(A, "u", "GATE-1", Ks03 with { ValidFraction = 0.5 });

        Assert.True(low.Insufficient);
        Assert.NotEqual(ok.InputSha256, low.InputSha256);
        Assert.Equal(ok.Id, low.Supersedes);
        Assert.Equal(ok.Value, low.Value);                                                                   // still scored
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public async Task AnImpossibleValidFractionIsRefused(double valid)
    {
        var (_, svc) = await NewAsync();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RecordAsync(A, "u", "BAD", Ks03 with { ValidFraction = valid }));
        Assert.Contains("invalid_valid_fraction", ex.Codes);
    }

    [Fact]
    public async Task WithNoEstablishedComponentThereIsNoResultToRecord()
    {
        var (k, svc) = await NewAsync();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RecordAsync(A, "u", "EMPTY", new CriFeatures(null, null, null, null, null, null, 0.9)));
        Assert.Contains("no_established_components", ex.Codes);
        Assert.Empty(await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "EMPTY"));
    }

    [Fact]
    public async Task ADifferentFeatureValueIsANewResultEvenWhenItClampsToTheSameScore()
    {
        var (k, svc) = await NewAsync();
        var a = await svc.RecordAsync(A, "u", "CLAMP", Ks03 with { NdmiMin = 10.0 });                          // below the ramp: score 85
        var b = await svc.RecordAsync(A, "u", "CLAMP", Ks03 with { NdmiMin = 5.0 });                           // further below: still 85

        Assert.Equal(a.Components.Single(c => c.Key == "water_security").Score, b.Components.Single(c => c.Key == "water_security").Score);
        Assert.NotEqual(a.InputSha256, b.InputSha256);
        Assert.Equal(a.Id, b.Supersedes);
        Assert.Equal((5.0, 8), (b.Components.Single(c => c.Key == "water_security").FeatureValue, b.ClosedSeasons));
    }
}
