using Zr3i.Data;

namespace Zr3i.Kernel.Findings;

/// <summary>
/// One statement the platform makes about one parcel in one season. Immutable (append-only): a finding freezes the claim it was made
/// against, the boundary, the observations, the method and the crop rule, so it can be reproduced and never points at live rows. New
/// inputs produce a new finding that supersedes the old one. It states a fact and its status; a person decides what to do.
/// </summary>
public sealed class Finding : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public string ParcelSeasonId { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;

    /// <summary>"area" or "crop": the question this finding answers. A newer finding in the same slot supersedes an older one.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>area_gap, crop_present, no_crop or not_assessable.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>MEASURED, FLAGGED, PENDING or NOT_ASSESSABLE.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>A stable code, or null when the finding needs no reason.</summary>
    public string? Reason { get; set; }

    // The customer's claim, frozen as it was when the finding was made.
    public string? DeclaredCrop { get; set; }
    public double? DeclaredAreaFeddan { get; set; }
    public string? DeclaredSowingDate { get; set; }
    public string? DeclaredSource { get; set; }

    // Area
    public double? MeasuredFeddan { get; set; }

    /// <summary>Signed: measured minus declared, as measured. Kept for the audit trail.</summary>
    public double? GapFeddan { get; set; }
    public double? GapPercent { get; set; }
    public double? NoisePercent { get; set; }
    public string? Direction { get; set; }

    // Crop evidence
    public int ScenesUsed { get; set; }
    public int PassesUsed { get; set; }
    public double? MaxNdvi { get; set; }
    public List<string> RulesTested { get; set; } = [];
    public List<string> ObservationIds { get; set; } = [];

    // Provenance
    public string BoundaryId { get; set; } = string.Empty;
    public string BoundaryGeometrySha256 { get; set; } = string.Empty;
    public string MethodId { get; set; } = string.Empty;
    public int MethodVersion { get; set; }

    /// <summary>"draft" until the method is frozen. A finding made under a draft method cannot go into a frozen report.</summary>
    public string MethodState { get; set; } = string.Empty;

    /// <summary>Hash of the method parameters this finding used (a draft can change without a new version).</summary>
    public string ParametersSha256 { get; set; } = string.Empty;
    public string? CropRuleSource { get; set; }
    public string? CropConfigId { get; set; }
    public int? CropConfigVersion { get; set; }
    public string? CropRuleSha256 { get; set; }

    /// <summary>Points at the accuracy register stratum this finding belongs to: method, type, crop and size class.</summary>
    public string AccuracyRef { get; set; } = string.Empty;

    /// <summary>Hash of everything above that decides the result and of the finding it replaces; the same inputs never add a second row.</summary>
    public string InputSha256 { get; set; } = string.Empty;
    public string? Supersedes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public static class FindingSlots
{
    public const string Area = "area";
    public const string Crop = "crop";
}
