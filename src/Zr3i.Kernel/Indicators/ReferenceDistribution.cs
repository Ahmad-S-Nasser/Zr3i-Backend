using Zr3i.Data;

namespace Zr3i.Kernel.Indicators;

public static class ReferenceKinds
{
    /// <summary>Pooled parcel working scores. Used to band a CRI.</summary>
    public const string Parcel = "parcel";

    /// <summary>Pooled farm working scores. A different object from the parcel one: a farm score is never percentiled against it.</summary>
    public const string Farm = "farm";
}

public static class CohortLevels
{
    public const string CropGovernorate = "crop_governorate";
    public const string CropRegion = "crop_region";
    public const string CropNational = "crop_national";
}

/// <summary>
/// A multi-season reference distribution of working scores for one cohort (methodology 10.3). Immutable and versioned: a rebuilt
/// distribution is a new version, and the old one stays so historical values can be reproduced.
/// </summary>
public sealed class ReferenceDistribution : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>parcel or farm.</summary>
    public string Kind { get; set; } = string.Empty;
    public string CohortLevel { get; set; } = string.Empty;

    /// <summary>For example "maize|kafr-el-sheikh". Free text chosen by the caller, compared exactly.</summary>
    public string CohortKey { get; set; } = string.Empty;
    public int Version { get; set; }

    /// <summary>Pooled working scores, sorted ascending.</summary>
    public List<double> Scores { get; set; } = [];

    /// <summary>The seasons pooled into the scores. At least five.</summary>
    public List<string> Seasons { get; set; } = [];
    public string ScoresSha256 { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
