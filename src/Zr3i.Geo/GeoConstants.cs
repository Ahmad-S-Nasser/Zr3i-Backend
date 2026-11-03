namespace Zr3i.Geo;

/// <summary>Fixed numbers behind every measured area. Changing any of them is a new area method id.</summary>
public static class GeoConstants
{
    /// <summary>Area is computed in this projected CRS (UTM 36N), never in degrees (CLAUDE.md).</summary>
    public const int AreaEpsg = 32636;

    public const double SquareMetresPerFeddan = 4200.83;

    /// <summary>Planar area in EPSG:32636, 4,200.83 m2 per feddan, polygon as stored (WGS84, vertices transformed).</summary>
    public const string AreaMethodId = "GEO-AREA-2026-01";

    /// <summary>Central meridian of UTM zone 36N.</summary>
    internal const double CentralMeridian = 33.0;
}
