using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class NdviSummaryTests
{
    private static readonly SceneRadiometry Applied = new(true, "05.13");

    [Fact]
    public void MedianMeanAndSpreadAreTakenOverTheUsablePixelsOnly()
    {
        // NIR 0.6 / red 0.1 gives 0.714...; NIR 0.5 / red 0.3 gives 0.25; NIR 0.4 / red 0.2 gives 0.333...
        var s = NdviSummary.Of([(6000, 1000), (5000, 3000), (4000, 2000)], Applied)!;
        Assert.Equal(3, s.Count);
        Assert.Equal(1.0 / 3, s.Median, 12);
        Assert.Equal((5.0 / 7 + 0.25 + 1.0 / 3) / 3, s.Mean, 12);
        Assert.Equal(0.25, s.Min, 12);
        Assert.Equal(5.0 / 7, s.Max, 12);
    }

    [Fact]
    public void AnEvenCountTakesTheMiddlePairAndNoDataIsLeftOut()
    {
        var s = NdviSummary.Of([(6000, 2000), (4000, 4000), (0, 1000), (5000, 0)], Applied)!;   // last two are DN 0: no data
        Assert.Equal(2, s.Count);
        Assert.Equal(2, s.NoData);
        Assert.Equal(0.25, s.Median, 12);                                                       // (0.5 + 0.0) / 2
    }

    [Fact]
    public void TheOffsetIsRemovedOnlyWhenTheProviderHasNotAlreadyDoneIt()
    {
        // Native baseline 05.13, offset not applied: (3000-1000, 1500-1000) -> 0.2 and 0.05 -> NDVI 0.6.
        var native = NdviSummary.Of([(3000, 1500)], new SceneRadiometry(false, "05.13"))!;
        Assert.Equal(0.6, native.Median, 12);
        var applied = NdviSummary.Of([(3000, 1500)], Applied)!;
        Assert.Equal(1.0 / 3, applied.Median, 12);
    }

    [Fact]
    public void ImplausibleReflectanceIsCountedAndExcludedNeverAveragedIn()
    {
        var s = NdviSummary.Of([(6000, 1000), (20000, 1000)], Applied)!;                        // 2.0 reflectance is not a real surface
        Assert.Equal(1, s.Count);
        Assert.Equal(1, s.Implausible);
    }

    [Fact]
    public void NothingValidGivesNoSummaryRatherThanZero() =>
        Assert.Null(NdviSummary.Of([(0, 0), (20000, 20000)], Applied));
}
