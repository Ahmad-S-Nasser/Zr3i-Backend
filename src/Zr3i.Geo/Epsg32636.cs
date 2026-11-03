using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace Zr3i.Geo;

/// <summary>WGS84 longitude/latitude to and from UTM 36N (EPSG:32636). Checked against GDAL/PROJ to 0.0001 m2 in the W1-5 spike.</summary>
public static class Epsg32636
{
    private static readonly ThreadLocal<(MathTransform Forward, MathTransform Inverse)> Transforms = new(() =>
    {
        var utm = ProjectedCoordinateSystem.WGS84_UTM(36, true);
        var factory = new CoordinateTransformationFactory();
        return (
            factory.CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, utm).MathTransform,
            factory.CreateFromCoordinateSystems(utm, GeographicCoordinateSystem.WGS84).MathTransform);
    });

    public static (double Easting, double Northing) FromLonLat(double lon, double lat)
    {
        var o = Transforms.Value.Forward.Transform([lon, lat]);
        return (o[0], o[1]);
    }

    public static (double Lon, double Lat) ToLonLat(double easting, double northing)
    {
        var o = Transforms.Value.Inverse.Transform([easting, northing]);
        return (o[0], o[1]);
    }
}
