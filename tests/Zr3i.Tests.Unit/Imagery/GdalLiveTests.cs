using Zr3i.Geo;
using Zr3i.Imagery;
using Xunit;

namespace Zr3i.Tests.Unit.Imagery;

/// <summary>
/// Reads the real Sentinel-2 scene with the real GDAL reader and checks it against what the W1-5 spike measured independently.
/// Off by default (needs the network and takes several seconds); run with ZR3I_NETWORK_TESTS=1. When off, the test returns at once.
/// </summary>
[Trait("Category", "Network")]
public class GdalLiveTests
{
    private const string Base = "https://sentinel-cogs.s3.us-west-2.amazonaws.com/sentinel-s2-l2a-cogs/36/R/TV/2026/10/S2C_36RTV_20261027_0_L2A/";

    [Fact]
    public async Task TheSpikeParcelReadsBackWithinTheRangesTheSpikeMeasured()
    {
        if (Environment.GetEnvironmentVariable("ZR3I_NETWORK_TESTS") != "1") return;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        Assert.True(GeoJsonReader.TryReadPolygon(File.ReadAllText(Path.Combine(dir!.FullName, "spikes", "data", "parcel-synthetic-zr-spike-01.geojson")), out var polygon, out _));
        var pixels = PixelTest.Evaluate(BoundaryValidator.Check(polygon!).Rings, keepPixels: true).Pixels!;
        Assert.Equal(337, pixels.Count);

        using var reader = new GdalBandReader();
        var red = await reader.ReadAsync(Base + "B04.tif", pixels);
        var scl = await reader.ReadAsync(Base + "SCL.tif", pixels);

        // The spike read the 422 pixels whose centre is inside the polygon: red 246 to 1266, classes 352 vegetation and 70 not vegetated.
        // The 337 whole pixels are a subset of those, so they must fall inside the same ranges.
        var dn = pixels.Select(p => red.ValueAt(p.Col, p.Row)).ToList();
        var classes = pixels.Select(p => scl.ValueAt(p.Col, p.Row)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        Assert.InRange(dn.Min(), 246, 1266);
        Assert.InRange(dn.Max(), 246, 1266);
        Assert.All(classes.Keys, c => Assert.Contains(c, new[] { 4, 5 }));
        Assert.Equal(337, classes.Values.Sum());
        Assert.True(classes.GetValueOrDefault(4) <= 352 && classes.GetValueOrDefault(5) <= 70);
        Assert.True(SceneReadability.Evaluate(pixels, (c, r) => scl.ValueAt(c, r)).Readable);
        Assert.InRange(red.Window.Width, 10, 27);                            // only the parcel's window was fetched, not the tile
        Assert.InRange(red.Window.Height, 10, 27);
        Assert.StartsWith("gdal 3.", reader.Name, StringComparison.Ordinal);

        // A second reader gets exactly the same numbers.
        using var second = new GdalBandReader();
        var again = await second.ReadAsync(Base + "B04.tif", pixels);
        Assert.Equal(red.Values, again.Values);
    }
}
