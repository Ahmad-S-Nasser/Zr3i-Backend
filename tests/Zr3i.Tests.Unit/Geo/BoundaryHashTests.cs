using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class BoundaryHashTests
{
    private static IReadOnlyList<IReadOnlyList<LonLat>> Ring(params (double Lon, double Lat)[] points) =>
        [points.Select(p => new LonLat(p.Lon, p.Lat)).ToList()];

    private static readonly (double, double)[] Square = [(31.0, 30.8), (31.001, 30.8), (31.001, 30.801), (31.0, 30.801), (31.0, 30.8)];

    [Fact]
    public void TheSameShapeAlwaysGivesTheSameHash()
    {
        Assert.Equal(BoundaryHash.Of(Ring(Square)), BoundaryHash.Of(Ring(Square)));
        Assert.Matches("^[0-9A-F]{64}$", BoundaryHash.Of(Ring(Square)));
    }

    [Fact]
    public void WhereTheRingStartsDoesNotChangeTheHash()
    {
        // Same square, written starting from a different corner.
        var rotated = new (double, double)[] { (31.001, 30.8), (31.001, 30.801), (31.0, 30.801), (31.0, 30.8), (31.001, 30.8) };

        Assert.Equal(BoundaryHash.Of(Ring(Square)), BoundaryHash.Of(Ring(rotated)));
    }

    [Fact]
    public void DifferencesBelowTheStoredPrecisionDoNotChangeTheHash()
    {
        var jitter = Square.Select(p => (p.Item1 + 1e-9, p.Item2 - 1e-9)).ToArray();

        Assert.Equal(BoundaryHash.Of(Ring(Square)), BoundaryHash.Of(Ring(jitter)));
    }

    [Fact]
    public void ARealMovementChangesTheHash()
    {
        var moved = Square.Select(p => (p.Item1 + 0.00001, p.Item2)).ToArray();   // about a metre

        Assert.NotEqual(BoundaryHash.Of(Ring(Square)), BoundaryHash.Of(Ring(moved)));
    }

    [Fact]
    public void HolesCountAndTheirOrderIsIrrelevant()
    {
        var hole1 = new LonLat[] { new(31.0002, 30.8002), new(31.0002, 30.8004), new(31.0004, 30.8004), new(31.0004, 30.8002), new(31.0002, 30.8002) };
        var hole2 = new LonLat[] { new(31.0006, 30.8006), new(31.0006, 30.8008), new(31.0008, 30.8008), new(31.0008, 30.8006), new(31.0006, 30.8006) };
        var outer = Ring(Square)[0];

        Assert.Equal(BoundaryHash.Of([outer, hole1, hole2]), BoundaryHash.Of([outer, hole2, hole1]));
        Assert.NotEqual(BoundaryHash.Of([outer]), BoundaryHash.Of([outer, hole1]));
    }
}
