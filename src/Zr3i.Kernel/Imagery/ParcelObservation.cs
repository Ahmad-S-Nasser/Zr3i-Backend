using Zr3i.Data;

namespace Zr3i.Kernel.Imagery;

public static class ObservationReasons
{
    /// <summary>Too small to test at 10 m (fewer than 8 whole pixels). Never to be shown as "no crop".</summary>
    public const string BelowResolution = "below_resolution";

    /// <summary>Too much of the parcel's pixels were lost in this scene (cloud, shadow, no data).</summary>
    public const string SceneLost = "scene_lost";
}

/// <summary>
/// What one scene says about one parcel's current boundary: how many of its whole pixels are usable and why a scene is or is not
/// readable. Immutable (append-only). It records where the answer came from: the scene and its item hash, the boundary version, the
/// methods, the reader, and a hash of the pixel values read, so the result can be checked by reading the same scene again.
/// Vegetation indices and statistics come later (W3-2); this is the readability record.
/// </summary>
public sealed class ParcelObservation : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public string BoundaryId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public DateTime SensingTime { get; set; }
    public int FullPixels { get; set; }
    public int UsablePixels { get; set; }
    public int LostPixels { get; set; }
    public double LostShare { get; set; }
    public bool Readable { get; set; }

    /// <summary>Null when readable; otherwise <see cref="ObservationReasons"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>Scene classification class (as text) to the number of the parcel's whole pixels in it.</summary>
    public Dictionary<string, int> SclHistogram { get; set; } = [];

    /// <summary>SHA-256 over the digital numbers of every whole pixel in all five bands, in pixel order. Null when no imagery was read (below resolution).</summary>
    public string? PixelStackSha256 { get; set; }

    // Features (W3-2). Only for a readable scene, over its usable pixels, from reflectance (offset rule in Reflectance). Null means "not
    // established" (unreadable scene, or no valid pixel), never zero. NDMI is not computed: its formula is still undecided in the method.
    public int? NdviPixels { get; set; }
    public int? NdviExcluded { get; set; }
    public double? NdviMedian { get; set; }
    public double? NdviMean { get; set; }
    public double? NdviMin { get; set; }
    public double? NdviMax { get; set; }

    // Provenance
    public string SceneItemSha256 { get; set; } = string.Empty;
    public List<string> Bands { get; set; } = [];
    public string PixelMethodId { get; set; } = string.Empty;
    public string MethodId { get; set; } = string.Empty;
    public int MethodVersion { get; set; }

    /// <summary>"draft" until the method is frozen. A result made under a draft method cannot go into a frozen report.</summary>
    public string MethodState { get; set; } = string.Empty;
    public string Reader { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public DateTime IngestedAt { get; set; }
}
