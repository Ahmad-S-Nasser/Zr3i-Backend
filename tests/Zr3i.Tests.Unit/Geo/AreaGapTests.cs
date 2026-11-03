using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

/// <summary>
/// Golden cases from Product Explained 3A.2: noise % = 100 x perimeter x boundary offset / area, with a 2 m offset as the
/// documented planning assumption. The rule is that a gap smaller than the field's own noise is never flagged (multiplier 1).
/// </summary>
public class AreaGapTests
{
    private const double Feddan = GeoConstants.SquareMetresPerFeddan;

    private static (double AreaM2, double PerimeterM) Square(double feddan)
    {
        var area = feddan * Feddan;
        return (area, 4 * Math.Sqrt(area));
    }

    [Theory]
    [InlineData(1.0, 1.0, 6.2)]      // the document's table, per 1 m of boundary offset
    [InlineData(5.0, 1.0, 2.8)]
    [InlineData(10.0, 1.0, 1.95)]
    [InlineData(1.0, 2.0, 12.3)]     // and at 2 m (the document rounds this to 12%)
    [InlineData(5.0, 2.0, 5.5)]
    [InlineData(10.0, 2.0, 3.9)]
    [InlineData(7.3, 2.0, 4.6)]      // CS-4471: "outside the +/-4.6% measurement noise"
    public void NoiseReproducesTheDocumentedTable(double feddan, double offsetM, double expectedPct)
    {
        var (area, perimeter) = Square(feddan);

        Assert.Equal(expectedPct, AreaGap.NoisePercent(area, perimeter, offsetM), tolerance: 0.1);
    }

    [Fact]
    public void ANarrowStripHasMoreNoiseThanASquareOfTheSameArea()
    {
        var strip = AreaGap.NoisePercent(30.0 * 140, 2 * (30 + 140), 1.0);

        Assert.Equal(8.1, strip, tolerance: 0.1);
        Assert.True(strip > AreaGap.NoisePercent(30.0 * 140, 4 * Math.Sqrt(30.0 * 140), 1.0));
    }

    [Fact]
    public void TheDocumentedWorkedExampleIsFlaggedAsOverDeclared()
    {
        var (measured, perimeter) = Square(7.3);

        var gap = AreaGap.Evaluate(measured, declaredFeddan: 10.0, perimeter, boundaryOffsetM: 2.0, flagMultiplier: 1.0);

        Assert.Equal(7.3, gap.MeasuredFeddan, precision: 6);
        Assert.Equal(-2.7, gap.GapFeddan, precision: 6);                 // signed: measured minus declared, kept for the audit
        Assert.Equal(27.0, gap.GapPercent, precision: 6);
        Assert.Equal(4.57, gap.NoisePercent, tolerance: 0.01);
        Assert.True(gap.Flagged);
        Assert.Equal(AreaGapDirection.OverDeclared, gap.Direction);
    }

    [Fact]
    public void AGapInsideTheNoiseIsNeverFlaggedAndOneWellOutsideIs()
    {
        var (m1, p1) = Square(9.8);                                      // 2% under the declared 10 feddan; the noise on this field is about 3.9%
        var inside = AreaGap.Evaluate(m1, 10.0, p1, 2.0, 1.0);
        var (m2, p2) = Square(9.0);                                      // 10% under; noise about 4.1%
        var outside = AreaGap.Evaluate(m2, 10.0, p2, 2.0, 1.0);

        Assert.False(inside.Flagged);
        Assert.Equal(AreaGapDirection.WithinNoise, inside.Direction);
        Assert.True(outside.Flagged);
        Assert.Equal(AreaGapDirection.OverDeclared, outside.Direction);
        Assert.True(inside.GapPercent < inside.NoisePercent && outside.GapPercent > outside.NoisePercent);
    }

    [Fact]
    public void TheRuleIsTwoSidedAndTheDirectionIsNamed()
    {
        var (measured, perimeter) = Square(13.0);

        var under = AreaGap.Evaluate(measured, declaredFeddan: 10.0, perimeter, 2.0, 1.0);

        Assert.True(under.Flagged);
        Assert.Equal(AreaGapDirection.UnderDeclared, under.Direction);   // measured is larger than declared
        Assert.True(under.GapFeddan > 0);
    }

    [Fact]
    public void AGapJustUnderTheThresholdDoesNotFlagAndJustOverDoesAndTheMultiplierMovesTheThreshold()
    {
        var (measured, perimeter) = Square(7.3);
        var noise = AreaGap.NoisePercent(measured, perimeter, 2.0);
        double DeclaredForGap(double percent) => measured / Feddan / (1 - percent / 100);       // declared area that makes the gap this many percent

        Assert.False(AreaGap.Evaluate(measured, DeclaredForGap(noise * 0.999), perimeter, 2.0, 1.0).Flagged);
        Assert.True(AreaGap.Evaluate(measured, DeclaredForGap(noise * 1.001), perimeter, 2.0, 1.0).Flagged);

        var oneAndAHalf = DeclaredForGap(noise * 1.5);
        Assert.True(AreaGap.Evaluate(measured, oneAndAHalf, perimeter, 2.0, 1.0).Flagged);       // multiplier 1: flagged
        Assert.False(AreaGap.Evaluate(measured, oneAndAHalf, perimeter, 2.0, 2.0).Flagged);      // multiplier 2: it would need a gap over twice the noise
    }

    [Theory]
    [InlineData(0.0, 100.0, 2.0, 1.0)]
    [InlineData(-1.0, 100.0, 2.0, 1.0)]
    [InlineData(5.0, 0.0, 2.0, 1.0)]
    [InlineData(5.0, 100.0, -1.0, 1.0)]
    [InlineData(5.0, 100.0, 2.0, 0.0)]
    [InlineData(double.NaN, 100.0, 2.0, 1.0)]
    public void NonsenseInputsAreRefusedRatherThanGivingANumber(double declared, double perimeter, double offset, double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AreaGap.Evaluate(30000, declared, perimeter, offset, multiplier));
    }
}
