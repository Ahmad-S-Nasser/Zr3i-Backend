using System.IO.Compression;
using Zr3i.Geo;
using Zr3i.Imagery;
using Xunit;

namespace Zr3i.Tests.Unit;

/// <summary>The PNG writer and the chip pictures: every colour is a design token, pixels the scene could not see are never drawn as ground, and the same input gives the same bytes.</summary>
public class ChipRenderingTests
{
    private static readonly SceneRadiometry Radiometry = new(true, "05.13");

    // ---- a tiny PNG reader, so the tests look at the pixels that were really written ----

    private static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int at = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png[(at + 8)..(at + 8 + length)];
            var crc = (uint)((png[at + 8 + length] << 24) | (png[at + 9 + length] << 16) | (png[at + 10 + length] << 8) | png[at + 11 + length]);
            Assert.Equal(crc, PngEncoder.Crc32(System.Text.Encoding.ASCII.GetBytes(type), data));
            if (type == "IHDR") { w = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]; h = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]; Assert.Equal((8, 2), (data[8], data[9])); }
            if (type == "IDAT") idat.Write(data);
            at += 12 + length;
        }

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(inflated);
        var raw = inflated.ToArray();
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(0, raw[y * (1 + w * 3)]);       // filter type: none
            Buffer.BlockCopy(raw, y * (1 + w * 3) + 1, rgb, y * w * 3, w * 3);
        }

        return (w, h, rgb);
    }

    private static (byte R, byte G, byte B) Px(byte[] png, int x, int y)
    {
        var (w, _, rgb) = Decode(png);
        var i = (y * w + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    // ---- the PNG writer ----

    [Fact]
    public void APngRoundTripsItsPixelsAndEveryChunkHasAValidCrc()
    {
        var rgb = new byte[4 * 3 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7);

        var (w, h, back) = Decode(PngEncoder.Encode(4, 3, rgb));

        Assert.Equal((4, 3), (w, h));
        Assert.Equal(rgb, back);
    }

    [Fact]
    public void APngWithTheWrongAmountOfPixelDataIsRefused() =>
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(2, 2, new byte[5]));

    [Fact]
    public void TheCrcOfTheStandardIendChunkIsTheKnownValue() =>
        Assert.Equal(0xAE426082u, PngEncoder.Crc32(System.Text.Encoding.ASCII.GetBytes("IEND"), []));

    // ---- the pictures ----

    /// <summary>A 5 by 5 window, scale 3, every pixel the same unless <paramref name="at"/> says otherwise. No outlines, so colours are the pixel colours.</summary>
    private static ChipInput Window(Func<int, int, ChipPixel> at, int scale = 3, IReadOnlyList<(double, double)>? ring = null, IReadOnlySet<(int, int)>? measured = null) =>
        new(100, 200, 5, 5, scale, at, Radiometry, ring ?? [], measured ?? new HashSet<(int, int)>());

    private static ChipPixel Pixel(int red, int nir, int scl = 4) => new(Blue: 500, Green: 900, Red: red, Nir: nir, Scl: scl);

    [Theory]
    [InlineData(1800, 2000, 0xC9, 0xA4, 0x5C)]   // NDVI 0.05: low
    [InlineData(2000, 4000, 0x9C, 0xBF, 0x6A)]   // NDVI 0.33: mid
    [InlineData(1000, 6000, 0x2F, 0x7A, 0x4A)]   // NDVI 0.71: high
    public void TheNdviPictureUsesTheThreeDesignTokenClasses(int red, int nir, int r, int g, int b)
    {
        var images = ChipRenderer.Render(Window((_, _) => Pixel(red, nir)));

        Assert.Equal(((byte)r, (byte)g, (byte)b), Px(images.Ndvi, 4, 4));
    }

    [Fact]
    public void TheClassEdgesAreWhereTheTokensSayTheyAre()
    {
        Assert.Equal(ChipRenderer.NdviLow, ChipRenderer.NdviClass(0.249));
        Assert.Equal(ChipRenderer.NdviMid, ChipRenderer.NdviClass(0.25));
        Assert.Equal(ChipRenderer.NdviMid, ChipRenderer.NdviClass(0.599));
        Assert.Equal(ChipRenderer.NdviHigh, ChipRenderer.NdviClass(0.6));
    }

    [Theory]
    [InlineData(3)]    // shadow
    [InlineData(8)]    // cloud, medium probability
    [InlineData(9)]    // cloud, high probability
    [InlineData(10)]   // thin cirrus
    [InlineData(0)]    // no data
    public void APixelTheSceneCouldNotSeeIsDrawnInTheUnusableToneAndNeverAsBareGround(int scl)
    {
        var images = ChipRenderer.Render(Window((_, _) => Pixel(1800, 2000, scl)));

        var seen = new HashSet<(byte, byte, byte)>();
        for (var y = 0; y < images.HeightPx; y++)
            for (var x = 0; x < images.WidthPx; x++) seen.Add(Px(images.Ndvi, x, y));
        Assert.Subset(new HashSet<(byte, byte, byte)> { ChipRenderer.UnusableFill, ChipRenderer.UnusableHatch }, seen);
        Assert.DoesNotContain(ChipRenderer.NdviLow, seen);
    }

    [Fact]
    public void OnlyThePixelsTheSceneCouldNotSeeAreHatchedAndTheRestKeepTheirClass()
    {
        // Column 102 (the third) is cloud; everything else is mid-class vegetation.
        var images = ChipRenderer.Render(Window((c, _) => c == 102 ? Pixel(1800, 2000, 9) : Pixel(2000, 4000)));

        Assert.Equal(ChipRenderer.NdviMid, Px(images.Ndvi, 1, 1));
        Assert.Contains(Px(images.Ndvi, 7, 7), new[] { ChipRenderer.UnusableFill, ChipRenderer.UnusableHatch });
        Assert.Equal(ChipRenderer.NdviMid, Px(images.Ndvi, 13, 7));
    }

    [Fact]
    public void TheTrueColourPictureShowsCloudAsItIsSoAPersonSeesWhatTheSatelliteSaw()
    {
        var white = Pixel(3000, 3000, 9) with { Blue = 3000, Green = 3000 };

        var images = ChipRenderer.Render(Window((_, _) => white));

        Assert.Equal(((byte)255, (byte)255, (byte)255), Px(images.TrueColour, 4, 4));
    }

    [Fact]
    public void NoDataIsBlackInTrueColour()
    {
        var images = ChipRenderer.Render(Window((_, _) => new ChipPixel(0, 0, 0, 0, 0)));

        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(images.TrueColour, 4, 4));
    }

    [Fact]
    public void TheHighestRowIsAtTheTopOfThePicture()
    {
        // Row 204 (the top row of the window) is high vegetation, every other row low.
        var images = ChipRenderer.Render(Window((_, r) => r == 204 ? Pixel(1000, 6000) : Pixel(1800, 2000)));

        Assert.Equal(ChipRenderer.NdviHigh, Px(images.Ndvi, 4, 1));
        Assert.Equal(ChipRenderer.NdviLow, Px(images.Ndvi, 4, 13));
    }

    [Fact]
    public void ThePicturesAreTheWindowTimesTheScaleAndTheSameSizeAsEachOther()
    {
        var images = ChipRenderer.Render(Window((_, _) => Pixel(2000, 4000), scale: 4));

        Assert.Equal((20, 20), (images.WidthPx, images.HeightPx));
        Assert.Equal((20, 20, 20 * 20 * 3), (Decode(images.TrueColour).Width, Decode(images.Ndvi).Height, Decode(images.TrueColour).Rgb.Length));
    }

    [Fact]
    public void TheDeclaredBoundaryIsDrawnSolidInTheBoundaryColourWithAHaloAndTheMeasuredFootprintDashedInItsOwnColour()
    {
        // A declared square from lattice (101,201) to (104,204) in metres, and the measured pixels inside it.
        (double, double)[] ring = [(1010, 2010), (1040, 2010), (1040, 2040), (1010, 2040), (1010, 2010)];
        var measured = new HashSet<(int, int)> { (102, 202), (102, 203), (103, 202), (103, 203) };

        var images = ChipRenderer.Render(Window((_, _) => Pixel(2000, 4000), scale: 6, ring: ring, measured: measured));

        var seen = new Dictionary<(byte, byte, byte), int>();
        for (var y = 0; y < images.HeightPx; y++)
            for (var x = 0; x < images.WidthPx; x++) seen[Px(images.Ndvi, x, y)] = seen.GetValueOrDefault(Px(images.Ndvi, x, y)) + 1;
        Assert.True(seen.GetValueOrDefault(ChipRenderer.Boundary) > 20, "the declared outline is drawn");
        Assert.True(seen.GetValueOrDefault(ChipRenderer.Halo) > 20, "with a halo");
        Assert.True(seen.GetValueOrDefault(ChipRenderer.Measured) > 3, "and the measured footprint is drawn");

        // Dashed: along the measured footprint's left edge some pixels are the footprint colour and some are not.
        var leftEdgeX = (102 - 100) * 6;
        var along = Enumerable.Range(0, images.HeightPx).Select(y => Px(images.Ndvi, leftEdgeX, y)).ToList();
        Assert.Contains(ChipRenderer.Measured, along);
        Assert.Contains(along, c => c != ChipRenderer.Measured);
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        ChipInput Input() => Window((c, r) => Pixel(1500 + c % 5 * 100, 3000 + r % 7 * 200), scale: 4, ring: [(1010, 2010), (1040, 2040)], measured: new HashSet<(int, int)> { (102, 202) });

        var a = ChipRenderer.Render(Input());
        var b = ChipRenderer.Render(Input());

        Assert.Equal(a.TrueColour, b.TrueColour);
        Assert.Equal(a.Ndvi, b.Ndvi);
    }

    [Fact]
    public void TheTokenColoursAreTheDesignSystemValues()
    {
        Assert.Equal(((byte)0xFF, (byte)0xD2, (byte)0x3F), ChipRenderer.Boundary);     // img-boundary
        Assert.Equal(((byte)0x3F, (byte)0xD0, (byte)0xFF), ChipRenderer.Measured);     // img-measured
        Assert.Equal(((byte)0xE8, (byte)0xEC, (byte)0xEE), ChipRenderer.UnusableFill); // not-assessable-bg
    }
}
