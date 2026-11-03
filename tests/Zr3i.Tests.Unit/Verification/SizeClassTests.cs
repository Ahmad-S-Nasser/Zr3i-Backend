using Zr3i.Modules.Verification;
using Xunit;

namespace Zr3i.Tests.Unit.Verification;

public class SizeClassTests
{
    [Fact]
    public void EdgesMakeContiguousClassesWithTheLowerEdgeIncluded()
    {
        var scheme = new SizeClassScheme([1, 3, 10]);

        Assert.Equal(["0-1", "1-3", "3-10", "10+"], scheme.Classes.Select(c => c.Key));
        Assert.Equal((0, 0, 1), (scheme.IndexOf(0.0), scheme.IndexOf(0.99), scheme.IndexOf(1.0)));
        Assert.Equal((1, 2, 3), (scheme.IndexOf(2.99), scheme.IndexOf(3.0), scheme.IndexOf(10.0)));
        Assert.Equal(3, scheme.IndexOf(5000));
        Assert.Null(scheme.Classes[^1].ToFeddan);
        Assert.Equal(1.0, scheme.Classes[1].FromFeddan);
    }

    [Theory]
    [InlineData(new double[] { })]
    [InlineData(new[] { 0.0, 3.0 })]
    [InlineData(new[] { -1.0, 3.0 })]
    [InlineData(new[] { 3.0, 3.0 })]
    [InlineData(new[] { 5.0, 3.0 })]
    [InlineData(new[] { 1.0, double.NaN })]
    [InlineData(new[] { 1.0, double.PositiveInfinity })]
    public void BadEdgesAreRefused(double[] edges)
    {
        Assert.Throws<ArgumentException>(() => new SizeClassScheme(edges));
    }

    [Fact]
    public void ShareIsReportedPerClassWithEmptyClassesKeptAndNeverPooled()
    {
        var scheme = new SizeClassScheme([1, 3]);
        var parcels = new[] { (0.2, false), (0.8, true), (1.5, true), (2.5, true), (7.0, false), (8.0, false), (9.0, true) };

        var shares = ReadableShare.ByClass(parcels, scheme);

        Assert.Equal(["0-1", "1-3", "3+"], shares.Select(s => s.Key));
        Assert.Equal([(2, 1), (2, 2), (3, 1)], shares.Select(s => (s.Parcels, s.Readable)));
        Assert.Equal([0.5, 1.0, 1.0 / 3], shares.Select(s => s.Share!.Value));
        Assert.DoesNotContain(typeof(ClassShare).GetProperties(), p => p.Name is "Overall" or "Total" or "Pooled");
    }

    [Fact]
    public void AClassWithNoParcelsHasNoShareRatherThanZero()
    {
        var shares = ReadableShare.ByClass([(0.5, true)], new SizeClassScheme([1, 3]));

        Assert.Null(shares[1].Share);
        Assert.Equal(0, shares[1].Parcels);
        Assert.Equal(1.0, shares[0].Share);
    }
}
