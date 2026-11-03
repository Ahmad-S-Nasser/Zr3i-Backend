namespace Zr3i.Geo;

/// <param name="Left">Easting of the raster's left edge in the tile's CRS.</param>
/// <param name="Top">Northing of the raster's top edge.</param>
/// <param name="PixelMetres">Size of one raster pixel (10 for the visible and near-infrared bands, 20 for the scene classification).</param>
public readonly record struct RasterGrid(double Left, double Top, double PixelMetres)
{
    /// <summary>
    /// The raster cell that holds the centre of a 10 m lattice pixel (the pixels the pixel test counts). Works for any band, so a 20 m
    /// classification pixel covers four lattice pixels and each of them looks up the one it sits in.
    /// </summary>
    public (int Col, int Row) CellOf(int latticeCol, int latticeRow)
    {
        var x = (latticeCol + 0.5) * PixelTestRules.PixelMetres;
        var y = (latticeRow + 0.5) * PixelTestRules.PixelMetres;
        return ((int)Math.Floor((x - Left) / PixelMetres), (int)Math.Floor((Top - y) / PixelMetres));
    }

    /// <summary>The smallest block of raster cells that holds all of the given lattice pixels: what to read from the file.</summary>
    public PixelWindow WindowOf(IEnumerable<(int Col, int Row)> latticePixels)
    {
        int minC = int.MaxValue, minR = int.MaxValue, maxC = int.MinValue, maxR = int.MinValue;
        foreach (var (c, r) in latticePixels)
        {
            var (col, row) = CellOf(c, r);
            minC = Math.Min(minC, col); maxC = Math.Max(maxC, col);
            minR = Math.Min(minR, row); maxR = Math.Max(maxR, row);
        }

        if (minC == int.MaxValue) throw new ArgumentException("There are no pixels to read.", nameof(latticePixels));
        return new PixelWindow(minC, minR, maxC - minC + 1, maxR - minR + 1);
    }
}

public readonly record struct PixelWindow(int XOff, int YOff, int Width, int Height)
{
    public int Cells => Width * Height;

    /// <summary>The index of a raster cell inside a buffer read for this window, row by row.</summary>
    public int IndexOf(int col, int row) => (row - YOff) * Width + (col - XOff);
}
