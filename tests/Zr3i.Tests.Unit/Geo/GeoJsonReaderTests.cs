using Zr3i.Geo;
using Xunit;

namespace Zr3i.Tests.Unit.Geo;

public class GeoJsonReaderTests
{
    private const string Ring = "[[31.0,30.8],[31.001,30.8],[31.001,30.801],[31.0,30.8]]";

    private static (bool Ok, PolygonInput? Polygon, string? Error) Read(string json)
    {
        var ok = GeoJsonReader.TryReadPolygon(json, out var polygon, out var error);
        return (ok, polygon, error);
    }

    [Theory]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[" + Ring + "]}")]
    [InlineData("{\"type\":\"Feature\",\"properties\":{},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[" + Ring + "]}}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[" + Ring + "]}}]}")]
    public void ReadsAPolygonInEachSupportedWrapper(string json)
    {
        var (ok, polygon, error) = Read(json);

        Assert.True(ok, error);
        Assert.Single(polygon!.Rings);
        Assert.Equal(4, polygon.Rings[0].Count);
        Assert.Equal(new LonLat(31.0, 30.8), polygon.Rings[0][0]);
    }

    [Fact]
    public void AltitudeInAPositionIsIgnored()
    {
        var (ok, polygon, _) = Read("{\"type\":\"Polygon\",\"coordinates\":[[[31.0,30.8,12.5],[31.001,30.8,12],[31.001,30.801,12],[31.0,30.8,12.5]]]}");

        Assert.True(ok);
        Assert.Equal(new LonLat(31.001, 30.8), polygon!.Rings[0][1]);
    }

    [Theory]
    [InlineData("{\"type\":\"Point\",\"coordinates\":[31.0,30.8]}", GeoJsonReader.NotAPolygon)]
    [InlineData("{\"type\":\"MultiPolygon\",\"coordinates\":[[" + Ring + "]]}", GeoJsonReader.NotAPolygon)]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[]}", GeoJsonReader.Invalid)]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[31.0],[31.1,30.8]]]}", GeoJsonReader.Invalid)]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[\"a\",\"b\"]]]}", GeoJsonReader.Invalid)]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[]}", GeoJsonReader.Invalid)]
    [InlineData("{\"type\":\"Polygon\"}", GeoJsonReader.Invalid)]
    [InlineData("[1,2,3]", GeoJsonReader.Invalid)]
    [InlineData("not json", GeoJsonReader.Invalid)]
    [InlineData("", GeoJsonReader.Invalid)]
    public void RefusesAnythingElseWithAStableCode(string json, string code)
    {
        var (ok, polygon, error) = Read(json);

        Assert.False(ok);
        Assert.Null(polygon);
        Assert.Equal(code, error);
    }

    [Fact]
    public void ACollectionOfTwoFeaturesIsRefusedBecauseItIsAmbiguous()
    {
        var feature = "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[" + Ring + "]}}";
        var (ok, _, error) = Read("{\"type\":\"FeatureCollection\",\"features\":[" + feature + "," + feature + "]}");

        Assert.False(ok);
        Assert.Equal(GeoJsonReader.Invalid, error);
    }
}
