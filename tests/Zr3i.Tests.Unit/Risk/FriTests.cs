using Zr3i.Kernel.Indicators;
using Zr3i.Modules.Risk;
using Xunit;

namespace Zr3i.Tests.Unit.Risk;

public class FriTests
{
    // Delta Agro Estate (methodology 9.2): nine parcels, 428.0 feddan. Each area x CRI product matches the published column.
    private static readonly FriParcel[] Delta =
    [
        new("ZR-KS-01", 62.4, 19.0, false), new("ZR-KS-02", 48.1, 26.0, false), new("ZR-KS-03", 55.7, 50.3, false),
        new("ZR-KS-04", 71.2, 33.0, false), new("ZR-KS-05", 39.8, 22.0, false), new("ZR-KS-06", 44.3, 58.0, false),
        new("ZR-KS-07", 52.6, 71.0, false), new("ZR-KS-08", 33.9, 29.0, false), new("ZR-KS-09", 20.0, 41.0, false),
    ];

    private static readonly IndicatorAdjustment[] DeltaAdjustments =
    [
        new(FriAdjustments.CropConcentration, 1.2), new(FriAdjustments.SpatialConcentration, 3.1), new(FriAdjustments.IrrigationDiversity, 2.0),
        new(FriAdjustments.RecordLength, -5.8), new(FriAdjustments.WorstParcelExposure, 1.2),
    ];

    [Fact]
    public void DeltaAgroEstateReproducesAwcri387AdjustmentsPlus17AndFri404()
    {
        var r = FriCalculator.Compute(Delta, DeltaAdjustments);

        Assert.Equal(428.0, r.TotalAreaFeddan, 9);
        Assert.Equal(16570.2, Delta.Sum(p => p.AreaFeddan * p.Cri), 1);   // the published column rounds each product to one decimal
        Assert.Equal(38.7, Math.Round(r.Awcri!.Value, 1));
        Assert.Equal(1.7, Math.Round(DeltaAdjustments.Sum(a => a.Value!.Value), 1));
        Assert.Equal(40.4, Math.Round(r.Fri!.Value, 1));
        Assert.Equal((9, 0), (r.Included, r.Excluded));
    }

    [Fact]
    public void TheRiceBlockAccountsForTheConcentrationInsightOfSection93()
    {
        var withoutRice = FriCalculator.Compute([.. Delta.Where(p => p.ParcelSeasonId != "ZR-KS-07")], []);
        Assert.Equal(34.2, Math.Round(withoutRice.Awcri!.Value, 1));
        Assert.Equal(12.3, Math.Round(100 * 52.6 / 428.0, 1));
    }

    [Fact]
    public void AnInsufficientParcelIsLeftOutOfTheMeanEntirelyAndItsAreaDoesNotWeighIt()
    {
        var parcels = new FriParcel[] { new("a", 10, 20, false), new("b", 30, 40, false), new("c", 1000, 99, true) };
        var r = FriCalculator.Compute(parcels, []);

        Assert.Equal((10 * 20 + 30 * 40) / 40.0, r.Awcri!.Value, 9);
        Assert.Equal((2, 1, 40.0, 1040.0), (r.Included, r.Excluded, r.IncludedAreaFeddan, r.TotalAreaFeddan));
    }

    [Fact]
    public void AdjustmentsAddToTheAwcriAndANullAdjustmentAddsNothing()
    {
        var parcels = new FriParcel[] { new("a", 10, 30, false) };
        var r = FriCalculator.Compute(parcels, [new(FriAdjustments.RecordLength, -2.5), new(FriAdjustments.CropConcentration, null)]);
        Assert.Equal((30.0, 27.5), (r.Awcri!.Value, r.Fri!.Value));
    }

    [Fact]
    public void WithNoParcelPassingTheGateThereIsNoFri()
    {
        var r = FriCalculator.Compute([new("a", 10, 30, true)], DeltaAdjustments);
        Assert.Equal((null, null, 0, 1), (r.Awcri, r.Fri, r.Included, r.Excluded));
        Assert.Null(FriCalculator.Compute([], []).Fri);
    }

    [Theory]
    [InlineData(0.0, "A")]
    [InlineData(19.999, "A")]
    [InlineData(20.0, "B")]
    [InlineData(39.999, "B")]
    [InlineData(40.0, "C")]
    [InlineData(59.999, "C")]
    [InlineData(60.0, "D")]
    [InlineData(79.999, "D")]
    [InlineData(80.0, "E")]
    [InlineData(100.0, "E")]
    public void TheBandIsAPureFunctionOfThePercentileWithHalfOpenRanges(double percentile, string band) =>
        Assert.Equal(band, Bands.FromPercentile(percentile));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonPercentileHasNoBand(double percentile) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Bands.FromPercentile(percentile));

    [Theory]
    [InlineData(5, 0)]          // below every reference score
    [InlineData(10, 25)]        // at or below: the score itself counts
    [InlineData(25, 50)]
    [InlineData(40, 100)]
    [InlineData(45, 100)]
    public void ThePercentileIsTheShareOfReferenceScoresAtOrBelowTheWorkingScore(double score, double expected) =>
        Assert.Equal(expected, Bands.Percentile([10, 20, 30, 40], score), 9);

    [Fact]
    public void TiesCountAsAtOrBelowAndAnEmptyReferenceIsRefused()
    {
        Assert.Equal(80.0, Bands.Percentile([10, 20, 20, 20, 40], 20), 9);                              // all three ties count as at or below
        Assert.Throws<ArgumentException>(() => Bands.Percentile([], 1));
    }
}
