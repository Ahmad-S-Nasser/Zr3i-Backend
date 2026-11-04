using Zr3i.Data;

namespace Zr3i.Kernel.Indicators;

/// <summary>One component of an indicator, with the feature (and its value) it was scored from. A component without data is stored with its weight and no score; it is never zero.</summary>
public sealed record IndicatorComponent(string Key, double Weight, double? Score, bool Established, string? Feature = null, double? FeatureValue = null);

/// <summary>A farm-level adjustment added to the area-weighted mean. A null value means it was not supplied and was not applied.</summary>
public sealed record IndicatorAdjustment(string Key, double? Value);

public static class IndicatorKinds
{
    public const string Cri = "CRI";
    public const string Fri = "FRI";
}

public static class IndicatorMaturity
{
    public const string Specified = "specified";
    public const string Calibrated = "calibrated";
    public const string Validated = "validated";
}

/// <summary>
/// An indicator result is immutable (append-only). It freezes the method version, the hash of its inputs, the value, every
/// component with its weight, and which components were missing. Recomputing with the same inputs gives the same hash and value
/// and adds no row; different inputs add a new result that supersedes the old one.
/// </summary>
public sealed class IndicatorResult : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>CRI or FRI.</summary>
    public string Indicator { get; set; } = string.Empty;

    /// <summary>parcel_season or farm_season.</summary>
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string MethodId { get; set; } = string.Empty;
    public int MethodVersion { get; set; }
    public string InputSha256 { get; set; } = string.Empty;
    public double Value { get; set; }
    public List<IndicatorComponent> Components { get; set; } = [];
    public List<string> MissingComponents { get; set; } = [];

    /// <summary>Closed harvest seasons that went into the yield history (CRI only). Null when none were supplied.</summary>
    public int? ClosedSeasons { get; set; }

    /// <summary>Farm-level adjustments (FRI only). Empty for a CRI.</summary>
    public List<IndicatorAdjustment> Adjustments { get; set; } = [];

    /// <summary>Usable share of the parcel's observations, and whether it fell below the validity floor (such a result is excluded from the farm roll-up).</summary>
    public double? ValidFraction { get; set; }
    public bool Insufficient { get; set; }

    /// <summary>specified, calibrated or validated. Always specified until calibration is done.</summary>
    public string Maturity { get; set; } = IndicatorMaturity.Specified;
    public string? Supersedes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
