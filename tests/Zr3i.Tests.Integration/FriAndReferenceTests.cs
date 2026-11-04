using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class FriAndReferenceTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("fri-a");
    private static readonly TenantScope B = TenantScope.ForTenant("fri-b");
    private static readonly string[] Seasons = ["2021-22", "2022-23", "2023-24", "2024-25", "2025-26"];

    private static readonly FriParcel[] Delta =
    [
        new("ZR-KS-01", 62.4, 19.0, false), new("ZR-KS-02", 48.1, 26.0, false), new("ZR-KS-03", 55.7, 50.3, false),
        new("ZR-KS-04", 71.2, 33.0, false), new("ZR-KS-05", 39.8, 22.0, false), new("ZR-KS-06", 44.3, 58.0, false),
        new("ZR-KS-07", 52.6, 71.0, false), new("ZR-KS-08", 33.9, 29.0, false), new("ZR-KS-09", 20.0, 41.0, false),
    ];

    private static readonly IndicatorAdjustment[] Adjustments =
    [
        new(FriAdjustments.CropConcentration, 1.2), new(FriAdjustments.SpatialConcentration, 3.1), new(FriAdjustments.IrrigationDiversity, 2.0),
        new(FriAdjustments.RecordLength, -5.8), new(FriAdjustments.WorstParcelExposure, 1.2),
    ];

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return k;
    }

    [Fact]
    public async Task DeltaAgroEstateIsRecordedAsAFrozenFarmResultWithEveryAdjustmentAndRecomputingAddsNothing()
    {
        var k = await NewAsync();
        var svc = new FriService(new IndicatorRecorder(k.Store, k.Clock));

        var first = await svc.RecordAsync(A, "u", "delta-2026", Delta, Adjustments);
        var again = await svc.RecordAsync(A, "u", "delta-2026", Delta, Adjustments);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(40.4, Math.Round(first.Value, 1));
        Assert.Equal(("FRI", "farm_season", "FRI-2026-01", "specified"), (first.Indicator, first.SubjectType, first.MethodId, first.Maturity));
        Assert.Equal(9, first.Components.Count);
        Assert.Equal(428.0, first.Components.Sum(c => c.Weight), 6);                                         // each parcel is weighted by its area
        Assert.Equal(5, first.Adjustments.Count);
        Assert.Equal(1.7, Math.Round(first.Adjustments.Sum(a => a.Value!.Value), 1));
        Assert.Single(await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "delta-2026"));
    }

    [Fact]
    public async Task AnExcludedParcelKeepsItsAreaAsAMissingComponentAndAnUnsuppliedAdjustmentIsStoredAsNotApplied()
    {
        var k = await NewAsync();
        var svc = new FriService(new IndicatorRecorder(k.Store, k.Clock));
        FriParcel[] parcels = [new("a", 10, 20, false), new("b", 30, 40, false), new("c", 5, 99, true)];

        var r = await svc.RecordAsync(A, "u", "partial", parcels, [new(FriAdjustments.RecordLength, -2.0)]);

        Assert.Equal(["parcel:c"], r.MissingComponents);
        Assert.Equal((5.0, null), (r.Components.Single(c => c.Key == "parcel:c").Weight, r.Components.Single(c => c.Key == "parcel:c").Score));
        Assert.Equal(35.0 - 2.0, r.Value, 9);
        Assert.Equal(5, r.Adjustments.Count);
        Assert.Equal(4, r.Adjustments.Count(a => a.Value is null));
    }

    [Fact]
    public async Task ChangingOneParcelsCriOrAnAdjustmentMakesANewResultThatSupersedesTheOld()
    {
        var k = await NewAsync();
        var svc = new FriService(new IndicatorRecorder(k.Store, k.Clock));
        var first = await svc.RecordAsync(A, "u", "chg", Delta, Adjustments);
        var second = await svc.RecordAsync(A, "u", "chg", Delta, [.. Adjustments.Take(4), new IndicatorAdjustment(FriAdjustments.WorstParcelExposure, 1.3)]);

        Assert.Equal(first.Id, second.Supersedes);
        Assert.NotEqual(first.InputSha256, second.InputSha256);
    }

    [Theory]
    [InlineData("invalid_parcel")]
    [InlineData("duplicate_parcel")]
    [InlineData("unknown_adjustment")]
    [InlineData("no_eligible_parcels")]
    public async Task BadFarmInputsAreRefusedWithACode(string code)
    {
        var k = await NewAsync();
        var svc = new FriService(new IndicatorRecorder(k.Store, k.Clock));
        FriParcel[] parcels = code switch
        {
            "invalid_parcel" => [new("a", 0, 20, false)],
            "duplicate_parcel" => [new("a", 1, 20, false), new("a", 2, 30, false)],
            "no_eligible_parcels" => [new("a", 1, 20, true)],
            _ => [new("a", 1, 20, false)],
        };
        IndicatorAdjustment[] adj = code == "unknown_adjustment" ? [new("made_up", 1.0)] : [];

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RecordAsync(A, "u", "bad", parcels, adj));
        Assert.Contains(code, ex.Codes);
    }

    private static List<double> Scores(int n, double from = 20, double step = 1) => [.. Enumerable.Range(0, n).Select(i => from + i * step)];

    [Fact]
    public async Task AReferenceDistributionIsStoredSortedHashedAndEachRebuildIsANewVersionThatLeavesTheOldRetrievable()
    {
        var k = await NewAsync();
        var refs = new ReferenceService(k.Store, k.Clock);
        var v1 = await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "maize|kafr", Seasons, [40, 20, 30]);
        var v2 = await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "maize|kafr", Seasons, [25, 35]);

        Assert.Equal([20.0, 30, 40], v1.Scores);
        Assert.Equal((1, 2), (v1.Version, v2.Version));
        Assert.Matches("^[0-9A-F]{64}$", v1.ScoresSha256);
        Assert.NotEqual(v1.ScoresSha256, v2.ScoresSha256);
        Assert.Equal([20.0, 30, 40], (await refs.GetVersionAsync(A, ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "maize|kafr", 1))!.Scores);
    }

    [Theory]
    [InlineData("invalid_kind")]
    [InlineData("invalid_cohort")]
    [InlineData("too_few_seasons")]
    [InlineData("no_scores")]
    [InlineData("invalid_score")]
    public async Task ABadReferenceIsRefusedWithACode(string code)
    {
        var k = await NewAsync();
        var refs = new ReferenceService(k.Store, k.Clock);
        Task<ReferenceDistribution> Add(string kind = ReferenceKinds.Parcel, string level = CohortLevels.CropNational, string[]? seasons = null, double[]? scores = null) =>
            refs.AddAsync(A, "u", kind, level, "k", seasons ?? Seasons, scores ?? [1, 2]);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => code switch
        {
            "invalid_kind" => Add(kind: "plot"),
            "invalid_cohort" => Add(level: "world"),
            "too_few_seasons" => Add(seasons: [.. Seasons.Take(4), "2021-22"]),   // a repeated season is not a fifth
            "no_scores" => Add(scores: []),
            _ => Add(scores: [1, double.NaN]),
        });
        Assert.Contains(code, ex.Codes);
    }

    [Fact]
    public async Task TheNarrowestCohortWithAtLeast30ScoresIsUsedOtherwiseTheCohortWidensAndTheOneUsedIsRecorded()
    {
        var k = await NewAsync();
        var refs = new ReferenceService(k.Store, k.Clock);
        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "maize|kafr", Seasons, Scores(10));
        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropRegion, "maize|delta", Seasons, Scores(40));
        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropNational, "maize", Seasons, Scores(500));
        CohortCandidate[] chain = [new(CohortLevels.CropGovernorate, "maize|kafr"), new(CohortLevels.CropRegion, "maize|delta"), new(CohortLevels.CropNational, "maize")];

        var widened = await refs.AssessAsync(A, ReferenceKinds.Parcel, chain, 30.0);
        Assert.Equal((CohortLevels.CropRegion, "maize|delta", 40), (widened!.CohortLevel, widened.CohortKey, widened.ReferenceSize));
        Assert.Equal(100.0 * 11 / 40, widened.Percentile, 9);                                                // scores 20..59; 20..30 are at or below 30
        Assert.Equal("B", widened.Band);

        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "maize|kafr", Seasons, Scores(30));
        var narrow = await refs.AssessAsync(A, ReferenceKinds.Parcel, chain, 30.0);
        Assert.Equal((CohortLevels.CropGovernorate, 2, 30), (narrow!.CohortLevel, narrow.ReferenceVersion, narrow.ReferenceSize));          // exactly 30 is enough
    }

    [Fact]
    public async Task WhenNoCohortReachesThirtyTheWidestExistingOneIsUsedAndWithNoReferenceThereIsNoBand()
    {
        var k = await NewAsync();
        var refs = new ReferenceService(k.Store, k.Clock);
        CohortCandidate[] chain = [new(CohortLevels.CropGovernorate, "rice|g"), new(CohortLevels.CropNational, "rice")];

        Assert.Null(await refs.AssessAsync(A, ReferenceKinds.Parcel, chain, 40));

        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropGovernorate, "rice|g", Seasons, Scores(12));
        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropNational, "rice", Seasons, Scores(20));
        var r = await refs.AssessAsync(A, ReferenceKinds.Parcel, chain, 40);
        Assert.Equal(CohortLevels.CropNational, r!.CohortLevel);
    }

    [Fact]
    public async Task AFarmScoreIsNeverBandedAgainstTheParcelReferenceAndOrganisationsAreSeparate()
    {
        var k = await NewAsync();
        var refs = new ReferenceService(k.Store, k.Clock);
        await refs.AddAsync(A, "u", ReferenceKinds.Parcel, CohortLevels.CropNational, "mixed", Seasons, Scores(100));
        CohortCandidate[] chain = [new(CohortLevels.CropNational, "mixed")];

        Assert.Null(await refs.AssessAsync(A, ReferenceKinds.Farm, chain, 40));                              // only a parcel reference exists
        Assert.Null(await refs.AssessAsync(B, ReferenceKinds.Parcel, chain, 40));                            // another organisation's
        Assert.NotNull(await refs.AssessAsync(A, ReferenceKinds.Parcel, chain, 40));
        Assert.Empty(await k.Store.ReferenceDistributions.FindAsync(B, _ => true));
    }
}
