using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class BoundaryValidatorTests
{
    // The synthetic spike parcel and its independent shapely+pyproj numbers (spikes/data), the golden case.
    private static PolygonInput SpikeParcel()
    {
        var path = Path.Combine(RepoRoot(), "spikes", "data", "parcel-synthetic-zr-spike-01.geojson");
        Assert.True(GeoJsonReader.TryReadPolygon(File.ReadAllText(path), out var polygon, out var error), error);
        return polygon!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }

    /// <summary>A polygon defined in metres on the EPSG:32636 grid, returned as the longitude/latitude a customer would send.</summary>
    private static PolygonInput FromUtm(params (double E, double N)[] points) =>
        PolygonInput.FromExterior(points.Append(points[0]).Select(p => { var (lon, lat) = Epsg32636.ToLonLat(p.E, p.N); return new LonLat(lon, lat); }));

    private static PolygonInput Square(double side, double e = 303800, double n = 3409200) =>
        FromUtm((e, n), (e + side, n), (e + side, n + side), (e, n + side));

    [Fact]
    public void SpikeParcelAreaMatchesTheIndependentCalculation()
    {
        var check = BoundaryValidator.Check(SpikeParcel());

        Assert.True(check.IsValid, string.Join(",", check.Errors));
        Assert.Equal(42009.08453946447, check.AreaM2, precision: 2);
        Assert.Equal(10.00018675820361, check.AreaFeddan, precision: 6);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void AKilometreSquareOnTheGridIsExactlyOneMillionSquareMetres()
    {
        var check = BoundaryValidator.Check(Square(1000));

        Assert.True(check.IsValid);
        Assert.Equal(1_000_000.0, check.AreaM2, precision: 2);
        Assert.Equal(1_000_000.0 / GeoConstants.SquareMetresPerFeddan, check.AreaFeddan, precision: 6);
    }

    [Fact]
    public void BoundsAndCentroidSitInsideTheParcel()
    {
        var check = BoundaryValidator.Check(SpikeParcel());

        Assert.InRange(check.Centroid.Lon, check.Bounds.MinLon, check.Bounds.MaxLon);
        Assert.InRange(check.Centroid.Lat, check.Bounds.MinLat, check.Bounds.MaxLat);
        Assert.InRange(check.Centroid.Lon, 30.9, 31.0);
        Assert.InRange(check.Centroid.Lat, 30.79, 30.81);
    }

    [Fact]
    public void ClockwiseInputIsNormalisedToCounterClockwiseWithTheSameArea()
    {
        var ccw = Square(200);
        var cw = PolygonInput.FromExterior(ccw.Rings[0].Reverse());

        var a = BoundaryValidator.Check(ccw);
        var b = BoundaryValidator.Check(cw);

        Assert.True(b.IsValid);
        Assert.Equal(a.AreaM2, b.AreaM2, precision: 6);
        Assert.True(BoundaryValidator.SignedArea(b.Rings[0]) > 0, "exterior ring should be counter-clockwise");
    }

    [Fact]
    public void ConsecutiveDuplicateVerticesAreDroppedQuietly()
    {
        var ring = Square(100).Rings[0].ToList();
        ring.Insert(2, ring[2]);
        ring.Insert(1, ring[1]);

        var check = BoundaryValidator.Check(PolygonInput.FromExterior(ring));

        Assert.True(check.IsValid);
        Assert.Equal(5, check.Rings[0].Count); // 4 corners and the closing vertex
    }

    [Fact]
    public void AnUnclosedRingIsRefused()
    {
        var open = Square(100).Rings[0].SkipLast(1);
        Assert.Equal(["ring_not_closed"], BoundaryValidator.Check(PolygonInput.FromExterior(open)).Errors);
    }

    [Fact]
    public void FewerThanThreeDistinctVerticesIsRefused()
    {
        var a = new LonLat(31.0, 30.8);
        var b = new LonLat(31.001, 30.8);
        Assert.Equal(["too_few_vertices"], BoundaryValidator.Check(PolygonInput.FromExterior([a, b, a])).Errors);
        Assert.Equal(["too_few_vertices"], BoundaryValidator.Check(new PolygonInput([])).Errors);
    }

    [Fact]
    public void ABowTieIsASelfIntersection()
    {
        var bowTie = FromUtm((303800, 3409200), (303900, 3409300), (303900, 3409200), (303800, 3409300));
        Assert.Equal(["self_intersection"], BoundaryValidator.Check(bowTie).Errors);
    }

    [Fact]
    public void ACollinearRingIsRefused()
    {
        var line = FromUtm((303800, 3409200), (303850, 3409200), (303900, 3409200));
        Assert.False(BoundaryValidator.Check(line).IsValid);
    }

    [Fact]
    public void AHoleInsideTheShellReducesTheAreaAndAHoleOutsideIsRefused()
    {
        var shell = Square(200).Rings[0];
        var inside = Square(50, 303875, 3409275).Rings[0];
        var outside = Square(50, 304500, 3409275).Rings[0];

        var withHole = BoundaryValidator.Check(new PolygonInput([shell, inside]));
        Assert.True(withHole.IsValid, string.Join(",", withHole.Errors));
        Assert.Equal(200.0 * 200 - 50.0 * 50, withHole.AreaM2, precision: 2);

        Assert.Equal(["hole_outside_shell"], BoundaryValidator.Check(new PolygonInput([shell, outside])).Errors);
    }

    [Theory]
    [InlineData(31.0, 95.0)]
    [InlineData(200.0, 30.8)]
    [InlineData(double.NaN, 30.8)]
    public void CoordinatesOutsideTheGlobeAreRefused(double lon, double lat)
    {
        var ring = new[] { new LonLat(lon, lat), new LonLat(31.01, 30.8), new LonLat(31.01, 30.81), new LonLat(lon, lat) };
        Assert.Equal(["coordinates_out_of_range"], BoundaryValidator.Check(PolygonInput.FromExterior(ring)).Errors);
    }

    [Fact]
    public void AParcelOutsideTheSupportedRegionIsRefused()
    {
        var paris = new[] { new LonLat(2.30, 48.85), new LonLat(2.31, 48.85), new LonLat(2.31, 48.86), new LonLat(2.30, 48.85) };
        Assert.Equal(["outside_supported_region"], BoundaryValidator.Check(PolygonInput.FromExterior(paris)).Errors);
    }

    [Fact]
    public void ASliverUnderOneSquareMetreIsDegenerate()
    {
        Assert.Equal(["degenerate"], BoundaryValidator.Check(FromUtm((303800, 3409200), (303800.5, 3409200), (303800, 3409200.5))).Errors);
    }

    [Fact]
    public void ABoundaryLargerThanTheLimitIsImplausible()
    {
        var huge = Square(Math.Sqrt(BoundaryRules.MaxAreaM2) * 1.1);
        Assert.Equal(["implausible_size"], BoundaryValidator.Check(huge).Errors);
    }

    [Fact]
    public void TooManyVerticesAreRefusedBeforeAnyHeavyWork()
    {
        var circle = Enumerable.Range(0, BoundaryRules.MaxVertices + 1)
            .Select(i => { var t = 2 * Math.PI * i / (BoundaryRules.MaxVertices + 1); return new LonLat(31.0 + 0.001 * Math.Cos(t), 30.8 + 0.001 * Math.Sin(t)); })
            .ToList();
        circle.Add(circle[0]);

        Assert.Equal(["too_many_vertices"], BoundaryValidator.Check(PolygonInput.FromExterior(circle)).Errors);
    }

    [Fact]
    public void FarFromTheCentralMeridianTheProjectionWarnsButStillMeasures()
    {
        var west = PolygonInput.FromExterior([new(26.00, 30.0), new(26.003, 30.0), new(26.003, 30.003), new(26.0, 30.003), new(26.0, 30.0)]);

        var check = BoundaryValidator.Check(west);

        Assert.True(check.IsValid);
        Assert.Contains("projection_distortion", check.Warnings);
    }

    [Fact]
    public void ACheckNeverThrowsOnHostileInput()
    {
        var nasty = new[]
        {
            new PolygonInput([[]]),
            new PolygonInput([[new LonLat(double.PositiveInfinity, 0)]]),
            PolygonInput.FromExterior([new(31, 30), new(31, 30), new(31, 30), new(31, 30)]),
        };
        foreach (var input in nasty) Assert.False(BoundaryValidator.Check(input).IsValid);
    }
}
