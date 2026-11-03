using Zr3i.Data;

namespace Zr3i.Kernel.Imagery;

/// <summary>
/// One satellite scene in the catalogue. Scenes are public facts, not any customer's data, so they live in the platform tenant and are
/// shared. Everything needed to find the scene again and to show where a result came from is kept: the provider's id, the item's hash,
/// and the links to the bands we read.
/// </summary>
public sealed class Scene : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The provider's scene id, for example S2C_36RTV_20261027_0_L2A. Unique in the catalogue.</summary>
    public string SceneId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTime SensingTime { get; set; }

    /// <summary>Military grid reference tile such as 36RTV.</summary>
    public string Tile { get; set; } = string.Empty;
    public int Epsg { get; set; }
    public double CloudCover { get; set; }
    public string ProcessingBaseline { get; set; } = string.Empty;

    /// <summary>The provider already removed the 1000-count offset (see Reflectance).</summary>
    public bool BoaOffsetApplied { get; set; }

    /// <summary>Band key (blue, green, red, nir, scl) to the link of its cloud-optimised GeoTIFF.</summary>
    public Dictionary<string, string> Assets { get; set; } = [];
    public double MinLon { get; set; }
    public double MinLat { get; set; }
    public double MaxLon { get; set; }
    public double MaxLat { get; set; }

    /// <summary>SHA-256 of the provider's item exactly as received. A different hash for the same scene id means the provider changed it.</summary>
    public string ItemSha256 { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public DateTime IngestedAt { get; set; }
}

/// <summary>A search area and period for scene discovery. The area is a box in longitude and latitude.</summary>
public sealed record SceneQuery(double MinLon, double MinLat, double MaxLon, double MaxLat, DateTime From, DateTime To, double MaxCloudCover = 100);

/// <summary>Where scenes come from. The catalogue and the jobs depend on this, not on any one provider.</summary>
public interface ISceneSource
{
    string Name { get; }

    Task<SceneSearchResult> SearchAsync(SceneQuery query, CancellationToken ct = default);
}

/// <param name="Skipped">Items the provider returned that cannot be used (a band we need is missing). Counted, never silently dropped.</param>
public sealed record SceneSearchResult(IReadOnlyList<Scene> Scenes, int Skipped);

public sealed record CatalogueChange(int Inserted, int Updated, int Unchanged);
