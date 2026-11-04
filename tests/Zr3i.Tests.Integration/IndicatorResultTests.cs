using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class IndicatorResultTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("ind-a");
    private static readonly TenantScope B = TenantScope.ForTenant("ind-b");

    private static IndicatorDraft Draft(string subject = "ps-1", double soil = 40, double value = 55.0) => new(
        "CRI", "parcel_season", subject, "FRI-2026-01", 1,
        [new("yield_stability", 25, 60, true), new("soil", 20, soil, true), new("climate", 15, null, false)],
        value);

    private async Task<(KernelHarness, IndicatorRecorder)> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return (k, new IndicatorRecorder(k.Store, k.Clock));
    }

    [Fact]
    public async Task RecordingTheSameInputsTwiceGivesTheSameHashAndValueAndOneRow()
    {
        var (k, rec) = await NewAsync();
        var first = await rec.RecordAsync(A, "u", Draft());
        var second = await rec.RecordAsync(A, "u", Draft());

        Assert.Equal(first.Id, second.Id);
        Assert.Equal((first.InputSha256, first.Value), (second.InputSha256, second.Value));
        Assert.Matches("^[0-9A-F]{64}$", first.InputSha256);
        Assert.Single(await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "ps-1"));
    }

    [Fact]
    public async Task ChangedInputsAddANewResultThatSupersedesTheOldAndTheOldIsUntouched()
    {
        var (k, rec) = await NewAsync();
        var first = await rec.RecordAsync(A, "u", Draft(soil: 40, value: 55.0));
        var second = await rec.RecordAsync(A, "u", Draft(soil: 70, value: 58.0));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.InputSha256, second.InputSha256);
        Assert.Equal(first.Id, second.Supersedes);
        var old = (await k.Store.IndicatorResults.GetAsync(A, first.Id))!;
        Assert.Equal((55.0, null), (old.Value, old.Supersedes));
        Assert.Equal(2, (await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "ps-1")).Count);
    }

    [Fact]
    public async Task ReturningToEarlierInputsIsANewResultNotARewriteOfHistory()
    {
        var (k, rec) = await NewAsync();
        await rec.RecordAsync(A, "u", Draft(soil: 40));
        await rec.RecordAsync(A, "u", Draft(soil: 70, value: 58));
        var third = await rec.RecordAsync(A, "u", Draft(soil: 40));

        Assert.Equal(3, (await k.Store.IndicatorResults.FindAsync(A, r => r.SubjectId == "ps-1")).Count);
        Assert.NotNull(third.Supersedes);
    }

    [Fact]
    public async Task AMissingComponentKeepsItsWeightIsListedAsMissingAndNeverBecomesZero()
    {
        var (_, rec) = await NewAsync();
        var r = await rec.RecordAsync(A, "u", Draft());

        var climate = r.Components.Single(c => c.Key == "climate");
        Assert.Equal((15.0, null, false), (climate.Weight, climate.Score, climate.Established));
        Assert.Equal(["climate"], r.MissingComponents);
        Assert.Equal(IndicatorMaturity.Specified, r.Maturity);
    }

    [Fact]
    public async Task ComponentOrderDoesNotChangeTheHash()
    {
        var (_, rec) = await NewAsync();
        var d = Draft();
        var reversed = d with { Components = [.. d.Components.Reverse()] };
        var a = await rec.RecordAsync(A, "u", d);
        var b = await rec.RecordAsync(B, "u", reversed);
        Assert.Equal(a.InputSha256, b.InputSha256);
    }

    [Fact]
    public async Task TheSameInputsWithADifferentValueAreRefusedBecauseTheCalculationIsNotDeterministic()
    {
        var (_, rec) = await NewAsync();
        await rec.RecordAsync(A, "u", Draft(value: 55.0));
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => rec.RecordAsync(A, "u", Draft(value: 56.0)));
        Assert.Contains("value_differs_for_same_inputs", ex.Codes);
    }

    [Theory]
    [InlineData("invalid_indicator")]
    [InlineData("invalid_weight")]
    [InlineData("duplicate_component")]
    [InlineData("score_missing_for_established")]
    [InlineData("score_present_for_not_established")]
    [InlineData("invalid_value")]
    public async Task BadDraftsAreRefusedWithACode(string code)
    {
        var (_, rec) = await NewAsync();
        var d = Draft();
        var bad = code switch
        {
            "invalid_indicator" => d with { Indicator = "SCORE" },
            "invalid_weight" => d with { Components = [new("x", 0, 10, true)] },
            "duplicate_component" => d with { Components = [new("x", 5, 1, true), new("x", 5, 1, true)] },
            "score_missing_for_established" => d with { Components = [new("x", 5, null, true)] },
            "score_present_for_not_established" => d with { Components = [new("x", 5, 3, false)] },
            _ => d with { Value = double.NaN },
        };
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => rec.RecordAsync(A, "u", bad));
        Assert.Contains(code, ex.Codes);
    }

    [Fact]
    public async Task OneOrganisationNeverSeesAnothersResultsAndTheStoreHasNoUpdateOrDelete()
    {
        var (k, rec) = await NewAsync();
        await rec.RecordAsync(A, "u", Draft());

        Assert.Empty(await k.Store.IndicatorResults.FindAsync(B, _ => true));
        var methods = typeof(AppendOnlyRepository<IndicatorResult>).GetMethods().Select(m => m.Name);
        Assert.DoesNotContain(methods, n => n.Contains("Update") || n.Contains("Delete") || n.Contains("Replace"));
    }
}
