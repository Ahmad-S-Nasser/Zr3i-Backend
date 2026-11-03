using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class RasterGridTests
{
    // Tile 36RTV as the W1-5 spike read it from the file header: left 199980, top 3500040.
    private static readonly RasterGrid TenMetre = new(199980, 3500040, 10);
    private static readonly RasterGrid TwentyMetre = new(199980, 3500040, 20);

    // The lattice pixel whose corner is at easting 303810, northing 3409210 (the spike parcel sits in this neighbourhood).
    private const int LatticeCol = 30381, LatticeRow = 340921;

    [Fact]
    public void ALatticePixelMapsToTheCellThatHoldsItsCentreInTheTenMetreBand()
    {
        // centre easting 303815: (303815 - 199980) / 10 = 10383.5 -> 10383; centre northing 3409215: (3500040 - 3409215) / 10 = 9082.5 -> 9082
        Assert.Equal((10383, 9082), TenMetre.CellOf(LatticeCol, LatticeRow));
    }

    [Fact]
    public void ALatticePixelMapsToTheTwentyMetreClassificationCellThatCoversIt()
    {
        // 103835 / 20 = 5191.75 -> 5191; 90825 / 20 = 4541.25 -> 4541
        Assert.Equal((5191, 4541), TwentyMetre.CellOf(LatticeCol, LatticeRow));
    }

    [Fact]
    public void FourNeighbouringLatticePixelsShareOneClassificationCell()
    {
        var cells = new[] { (0, 0), (1, 0), (0, 1), (1, 1) }
            .Select(d => TwentyMetre.CellOf(LatticeCol - LatticeCol % 2 + d.Item1, LatticeRow - LatticeRow % 2 + d.Item2))
            .Distinct()
            .ToList();

        Assert.Single(cells);
    }

    [Fact]
    public void NorthIsUpSoARowFurtherNorthHasASmallerRasterRow()
    {
        Assert.True(TenMetre.CellOf(LatticeCol, LatticeRow + 1).Row < TenMetre.CellOf(LatticeCol, LatticeRow).Row);
        Assert.True(TenMetre.CellOf(LatticeCol + 1, LatticeRow).Col > TenMetre.CellOf(LatticeCol, LatticeRow).Col);
    }

    [Fact]
    public void TheWindowIsTheSmallestBlockThatHoldsEveryPixelAndIndexesInRowOrder()
    {
        var pixels = new[] { (LatticeCol, LatticeRow), (LatticeCol + 3, LatticeRow + 2), (LatticeCol + 1, LatticeRow + 1) };

        var w = TenMetre.WindowOf(pixels);

        // columns 10383..10386 -> 4 wide; rows (north is up) 9080..9082 -> 3 high
        Assert.Equal((10383, 9080, 4, 3), (w.XOff, w.YOff, w.Width, w.Height));
        Assert.Equal(12, w.Cells);
        var (col, row) = TenMetre.CellOf(LatticeCol + 3, LatticeRow + 2);
        Assert.Equal(3, w.IndexOf(col, row));                       // top row, last column
        Assert.Equal(0, w.IndexOf(10383, 9080));
        Assert.Equal(11, w.IndexOf(10386, 9082));
    }

    [Fact]
    public void AWindowOverTheSpikeParcelIsSmallAndNeverEmpty()
    {
        // The parcel's 337 whole pixels, computed from its real boundary, fit a window of a few hundred cells (the spike read 27 x 26).
        var path = Path.Combine(RepoRoot(), "spikes", "data", "parcel-synthetic-zr-spike-01.geojson");
        Assert.True(GeoJsonReader.TryReadPolygon(File.ReadAllText(path), out var polygon, out _));
        var pixels = PixelTest.Evaluate(BoundaryValidator.Check(polygon!).Rings, keepPixels: true).Pixels!;

        var w = TenMetre.WindowOf(pixels);

        Assert.Equal(337, pixels.Count);
        Assert.InRange(w.Width, 15, 27);
        Assert.InRange(w.Height, 15, 27);
        Assert.True(pixels.Count <= w.Cells);
        Assert.All(pixels, p => { var (c, r) = TenMetre.CellOf(p.Col, p.Row); Assert.InRange(w.IndexOf(c, r), 0, w.Cells - 1); });
        Assert.Throws<ArgumentException>(() => TenMetre.WindowOf([]));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }
}
