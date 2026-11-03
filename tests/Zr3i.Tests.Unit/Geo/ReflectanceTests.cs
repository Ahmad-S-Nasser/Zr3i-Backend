using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class ReflectanceTests
{
    [Fact]
    public void WhenTheOffsetWasAlreadyAppliedTheValueIsJustScaled()
    {
        var scene = new SceneRadiometry(BoaOffsetApplied: true, ProcessingBaseline: "05.13");

        Assert.Equal(0.054, Reflectance.FromDn(540, scene)!.Value, precision: 6);
        Assert.Equal(0.8, Reflectance.FromDn(8000, scene)!.Value, precision: 6);
    }

    [Theory]
    [InlineData("04.00", 2500, 0.15)]
    [InlineData("05.13", 2500, 0.15)]
    [InlineData("05.13", 1000, 0.0)]
    public void ANativeProductFromBaseline04OnwardsHasTheThousandCountOffsetRemoved(string baseline, int dn, double expected)
    {
        Assert.Equal(expected, Reflectance.FromDn(dn, new SceneRadiometry(false, baseline))!.Value, precision: 6);
    }

    [Theory]
    [InlineData("03.00")]
    [InlineData("02.11")]
    public void OlderBaselinesHadNoOffset(string baseline)
    {
        Assert.Equal(0.25, Reflectance.FromDn(2500, new SceneRadiometry(false, baseline))!.Value, precision: 6);
    }

    [Fact]
    public void ZeroIsNoDataAndAnUnknownBaselineIsRefusedRatherThanGuessed()
    {
        Assert.Null(Reflectance.FromDn(0, new SceneRadiometry(true, "05.13")));
        Assert.Throws<ArgumentException>(() => Reflectance.FromDn(2500, new SceneRadiometry(false, null)));
        Assert.Throws<ArgumentException>(() => Reflectance.FromDn(2500, new SceneRadiometry(false, "not-a-baseline")));
    }

    [Fact]
    public void AMissingOffsetCannotMakeAValidPixelNegativeUnnoticed()
    {
        // With the offset wrongly left in, a red value of 540 would be -0.046: impossible for a real surface. The conversion reports it.
        var wrong = Reflectance.FromDn(540, new SceneRadiometry(false, "05.13"))!.Value;

        Assert.True(wrong < 0);
        Assert.False(Reflectance.IsPlausible(wrong));
        Assert.True(Reflectance.IsPlausible(0.054));
    }

    [Fact]
    public void NdviAndNdmiAreNormalisedDifferences()
    {
        Assert.Equal(0.8, Indices.NormalisedDifference(0.45, 0.05)!.Value, precision: 6);       // (0.45 - 0.05) / (0.45 + 0.05)
        Assert.Equal(-0.2, Indices.NormalisedDifference(0.2, 0.3)!.Value, precision: 6);
        Assert.Null(Indices.NormalisedDifference(0.0, 0.0));                                    // no signal, no index
    }
}
