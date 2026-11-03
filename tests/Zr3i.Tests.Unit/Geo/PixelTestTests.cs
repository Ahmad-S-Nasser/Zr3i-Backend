using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class PixelTestTests
{
    // Sentinel-2 pixels sit on a 10 m lattice in the UTM grid. These corners are multiples of 10, so shapes built from them are
    // exactly aligned to pixels and the expected counts can be worked out by hand.
    private const double E0 = 303800, N0 = 3409200;

    private static IReadOnlyList<IReadOnlyList<LonLat>> Rect(double x0, double y0, double x1, double y1) =>
        [new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0) }
            .Select(p => { var (lon, lat) = Epsg32636.ToLonLat(E0 + p.Item1, N0 + p.Item2); return new LonLat(lon, lat); }).ToList()];

    [Fact]
    public void TheRulesArePinned()
    {
        Assert.Equal(("GEO-PIXEL-2026-01", 5.0, 10.0, 8), (PixelTestRules.MethodId, PixelTestRules.BufferMetres, PixelTestRules.PixelMetres, PixelTestRules.MinFullPixels));
        Assert.Equal(0.6, PixelTestRules.MaxLostShare);
        Assert.Equal([4, 5, 6], PixelTestRules.UsableClasses);
    }

    [Fact]
    public void ABufferedAlignedSquareCountsExactlyTheWholePixelsLeft()
    {
        // 100 m square -> 90 m after the 5 m buffer, offset 5 m from the lattice -> 8 x 8 whole pixels (not 9 x 9).
        Assert.Equal(64, PixelTest.Evaluate(Rect(0, 0, 100, 100)).FullPixels);
        // Starting 5 m in, the buffered edge lands on the lattice: 90 m -> 9 x 9.
        Assert.Equal(81, PixelTest.Evaluate(Rect(5, 5, 105, 105)).FullPixels);
    }

    [Fact]
    public void ThePixelsAreReportedWithTheirGridPositions()
    {
        var result = PixelTest.Evaluate(Rect(5, 5, 55, 35), keepPixels: true);   // buffered: x 10..50, y 10..30 -> 4 x 2

        Assert.Equal(8, result.FullPixels);
        Assert.Equal(8, result.Pixels!.Count);
        Assert.Equal(((int)((E0 + 10) / 10), (int)((N0 + 10) / 10)), result.Pixels.Min());
        Assert.Null(PixelTest.Evaluate(Rect(5, 5, 55, 35)).Pixels);
    }

    [Theory]
    [InlineData(85, 25, 7, false)]      // 7 x 1 whole pixels: one short
    [InlineData(95, 25, 8, true)]       // 8 x 1: exactly the minimum passes
    [InlineData(55, 35, 8, true)]       // 4 x 2
    [InlineData(55, 34, 4, false)]      // 4 x 1 once the height drops below two pixels
    [InlineData(35, 35, 4, false)]      // 2 x 2
    public void AParcelPassesWithAtLeastEightWholePixels(double x1, double y1, int expected, bool passes)
    {
        var result = PixelTest.Evaluate(Rect(5, 5, x1, y1));

        Assert.Equal(expected, result.FullPixels);
        Assert.Equal(passes, result.Passes);
    }

    [Fact]
    public void ATinyParcelHasNoWholePixelsAndAGapDoesNotCount()
    {
        Assert.Equal(0, PixelTest.Evaluate(Rect(0, 0, 8, 8)).FullPixels);              // buffer eats it entirely
        Assert.False(PixelTest.Evaluate(Rect(0, 0, 8, 8)).Passes);

        // A 100 m square with a 40 m hole: pixels over the hole (and within 5 m of its edge) are not whole pixels inside.
        var withHole = new[] { Rect(5, 5, 105, 105)[0], Rect(35, 35, 75, 75)[0].AsEnumerable().Reverse().ToList() };
        var solid = PixelTest.Evaluate(Rect(5, 5, 105, 105)).FullPixels;
        var holed = PixelTest.Evaluate(withHole).FullPixels;

        Assert.Equal(81, solid);
        Assert.Equal(81 - 25, holed);   // the 40 m hole grows to 50 m with the buffer: 5 x 5 pixels are lost
    }

    [Fact]
    public void TheSpikeParcelGivesTheNumberTheIndependentCalculationGave()
    {
        var path = Path.Combine(RepoRoot(), "spikes", "data", "parcel-synthetic-zr-spike-01.geojson");
        Assert.True(GeoJsonReader.TryReadPolygon(File.ReadAllText(path), out var polygon, out _));
        var check = BoundaryValidator.Check(polygon!);

        var result = PixelTest.Evaluate(check.Rings);

        Assert.Equal(337, result.FullPixels);       // shapely: whole pixels inside the 5 m inward polygon; centre-inside would be 381
        Assert.True(result.Passes);
    }

    [Fact]
    public void ASmallerParcelNeverHasMoreWholePixelsThanALargerOneAroundIt()
    {
        var counts = Enumerable.Range(2, 30).Select(i => PixelTest.Evaluate(Rect(0, 0, i * 5, i * 5)).FullPixels).ToList();

        Assert.Equal(counts.Order(), counts);
        Assert.True(counts[^1] > counts[0]);
    }

    [Fact]
    public void SceneLossIsMeasuredOnTheWholePixelsAgainstTheUsableClasses()
    {
        var pixels = Enumerable.Range(0, 10).Select(i => (i, 0)).ToList();
        int? Classes(int col, int row) => col switch { < 4 => 4, 4 => 5, 5 => 6, 6 => 9, 7 => 3, 8 => 8, _ => (int?)null };   // 6 usable, 4 lost

        var scene = SceneReadability.Evaluate(pixels, Classes);

        Assert.Equal((6, 4, 0.4, true), (scene.Usable, scene.Lost, scene.LostShare, scene.Readable));
    }

    [Theory]
    [InlineData(4, false)]    // 6 lost of 10 = 60%: the rule is "less than 60%", so the limit itself fails
    [InlineData(3, false)]
    [InlineData(5, true)]     // 5 lost of 10 = 50%
    public void SixtyPercentLostIsTheLimitAndTheLimitItselfFails(int usable, bool readable)
    {
        var pixels = Enumerable.Range(0, 10).Select(i => (i, 0)).ToList();
        int? Classes(int col, int row) => col < usable ? 4 : 9;

        var scene = SceneReadability.Evaluate(pixels, Classes);

        Assert.Equal(readable, scene.Readable);
    }

    [Fact]
    public void ASceneWithNoWholePixelsOrNoDataIsNeverReadable()
    {
        Assert.False(SceneReadability.Evaluate([], (_, _) => 4).Readable);
        Assert.False(SceneReadability.Evaluate([(0, 0), (1, 0)], (_, _) => null).Readable);          // all no data
        Assert.False(SceneReadability.Evaluate([(0, 0), (1, 0)], (_, _) => 0).Readable);             // class 0 is "no data"
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }
}
