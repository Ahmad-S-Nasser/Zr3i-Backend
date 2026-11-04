using Zr3i.Modules.Risk;
using Xunit;

namespace Zr3i.Tests.Unit.Risk;

public class CriTests
{
    // Eight closed seasons, median 100, two below 80% of it, worst at 73.5%, population cv about 0.131 (methodology 8.1).
    private static readonly double[] Ks03Yields = [73.5, 79, 97, 100, 100, 104, 108, 112];

    private static CriFeatures Ks03(double validFraction = 0.9) => new(
        NdmiMin: 24.0, HeatStress: 0.58, NdviIntegral: 46.0, AreaVariancePct: 12.0, NdviCohortPercentile: 34.0, ClosedSeasonYields: Ks03Yields, ValidFraction: validFraction);

    [Theory]
    [InlineData(15, 85)]      // driest end of the water ramp
    [InlineData(45, 25)]
    [InlineData(0, 85)]       // clamped below
    [InlineData(80, 25)]      // clamped above
    [InlineData(24, 67)]      // worked example
    [InlineData(30, 55)]      // midpoint
    public void TheWaterRampScoresLinearlyBetweenItsPointsAndClampsOutsideThem(double ndmi, double expected) =>
        Assert.Equal(expected, ComponentScores.WaterSecurity(ndmi)!.Value, 9);

    [Fact]
    public void EachOtherRampHitsItsStatedPoints()
    {
        Assert.Equal((20.0, 75.0), (ComponentScores.ClimateExposure(0)!.Value, ComponentScores.ClimateExposure(1)!.Value));
        Assert.Equal(51.9, ComponentScores.ClimateExposure(0.58)!.Value, 9);
        Assert.Equal((75.0, 35.0), (ComponentScores.SoilAndLand(20)!.Value, ComponentScores.SoilAndLand(55)!.Value));
        Assert.Equal((55.0, 25.0), (ComponentScores.ManagementQuality(25)!.Value, ComponentScores.ManagementQuality(55)!.Value));
        Assert.Equal((25.0, 70.0, 70.0), (ComponentScores.TenureAndRights(0)!.Value, ComponentScores.TenureAndRights(25)!.Value, ComponentScores.TenureAndRights(100)!.Value));
        Assert.Equal((75.0, 25.0), (ComponentScores.PeerAndContext(0)!.Value, ComponentScores.PeerAndContext(100)!.Value));
        Assert.Equal(46.6, ComponentScores.TenureAndRights(12)!.Value, 9);
        Assert.Equal(58.0, ComponentScores.PeerAndContext(34)!.Value, 9);
        Assert.Equal(34.0, ComponentScores.ManagementQuality(46)!.Value, 9);
    }

    [Fact]
    public void ANullFeatureIsNotEstablishedNeverADefault()
    {
        Assert.Null(ComponentScores.WaterSecurity(null));
        Assert.Null(ComponentScores.ClimateExposure(null));
        Assert.Null(ComponentScores.SoilAndLand(null));
        Assert.Null(ComponentScores.ManagementQuality(null));
        Assert.Null(ComponentScores.TenureAndRights(null));
        Assert.Null(ComponentScores.PeerAndContext(null));
        Assert.Null(ComponentScores.YieldStability(null));
    }

    [Fact]
    public void YieldStabilityFollowsTheFormulaAndNeedsTwoClosedSeasons()
    {
        Assert.Equal(34.85, ComponentScores.YieldStability(Ks03Yields)!.Value, 1);
        Assert.Equal(15.0, ComponentScores.YieldStability([100, 100, 100])!.Value, 9);                      // perfect record: the floor
        Assert.Null(ComponentScores.YieldStability([100]));
        Assert.Null(ComponentScores.YieldStability([]));
        Assert.Null(ComponentScores.YieldStability([0, 0, 0]));                                              // no usable median

        // 80 percent of the median is not a shortfall; just below it is.
        Assert.Equal(15.0 + 0 * 45 + 0.2 * 25 + Cv([80, 100, 100]) * 15, ComponentScores.YieldStability([80, 100, 100])!.Value, 9);
        Assert.Equal(15.0 + (1.0 / 3) * 45 + 0.2001 * 25 + Cv([79.99, 100, 100]) * 15, ComponentScores.YieldStability([79.99, 100, 100])!.Value, 2);
        // cv is capped at 1 and the result at 100.
        Assert.Equal(15 + 0.5 * 45 + 1.0 * 25 + 1.0 * 15, ComponentScores.YieldStability([0, 1000])!.Value, 9);
    }

    private static double Cv(double[] y)
    {
        var mean = y.Average();
        return Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / y.Length) / mean;                             // population deviation
    }

    [Fact]
    public void ParcelZrKs03ReproducesCri503AndEveryContribution()
    {
        var r = CriCalculator.Compute(Ks03());

        Assert.Equal(50.3, Math.Round(r.Value!.Value, 1));
        Assert.Equal((7, 100.0), (r.EstablishedCount, r.EstablishedWeight));
        var contribution = r.Components.ToDictionary(c => c.Key, c => Math.Round(c.Score!.Value * c.Weight / r.EstablishedWeight, 2));
        Assert.Equal(16.75, contribution["water_security"]);
        Assert.Equal(10.38, contribution["climate_exposure"]);
        Assert.Equal(6.79, contribution["soil_and_land"]);
        Assert.Equal(5.22, contribution["yield_stability"], 1);   // the methodology rounds the score to 34.8; the yield series here is constructed, not published
        Assert.Equal(3.40, contribution["management_quality"]);
        Assert.Equal(3.73, contribution["tenure_and_rights"]);
        Assert.Equal(4.06, contribution["peer_and_context"]);
        Assert.Equal([25.0, 20, 15, 15, 10, 8, 7], r.Components.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Weight).OrderByDescending(w => w).ToArray());
    }

    [Theory]
    [InlineData(7, 100.0)]
    [InlineData(6, 85.0)]
    [InlineData(5, 78.0)]
    [InlineData(3, 50.0)]
    public void MissingComponentsKeepTheirWeightAndTheMeanIsRenormalisedOverWhatIsEstablished(int established, double weight)
    {
        // Established weights: 85 (no yield), 78 (also no peer), 50 (also no climate, no tenure).
        var f = Ks03() with { ClosedSeasonYields = established < 7 ? null : Ks03Yields };
        if (established <= 5) f = f with { NdviCohortPercentile = null };
        if (established <= 3) f = f with { HeatStress = null, AreaVariancePct = null };

        var r = CriCalculator.Compute(f);

        Assert.Equal((established, weight), (r.EstablishedCount, r.EstablishedWeight));
        Assert.Equal(7, r.Components.Count);
        Assert.Equal(100.0, r.Components.Sum(c => c.Weight));                                                // every weight is still carried
        Assert.All(r.Components.Where(c => !c.Established), c => Assert.Null(c.Score));
        var expected = r.Components.Where(c => c.Established).Sum(c => c.Score!.Value * c.Weight) / weight;
        Assert.Equal(expected, r.Value!.Value, 9);
    }

    [Fact]
    public void WithNothingEstablishedThereIsNoValue()
    {
        var r = CriCalculator.Compute(new CriFeatures(null, null, null, null, null, null, 0.9));
        Assert.Null(r.Value);
        Assert.Equal((0, 0.0), (r.EstablishedCount, r.EstablishedWeight));
    }

    [Theory]
    [InlineData(0.60, false)]
    [InlineData(0.5999, true)]
    [InlineData(1.0, false)]
    [InlineData(0.0, true)]
    public void AParcelBelowSixtyPercentValidIsInsufficientButStillScored(double valid, bool insufficient)
    {
        var r = CriCalculator.Compute(Ks03(valid));
        Assert.Equal(insufficient, r.Insufficient);
        Assert.NotNull(r.Value);
    }
}
