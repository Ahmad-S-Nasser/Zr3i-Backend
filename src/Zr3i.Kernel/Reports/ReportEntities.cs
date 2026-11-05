using Zr3i.Data;

namespace Zr3i.Kernel.Reports;

public static class ReportKinds
{
    /// <summary>A bank's tranche: verdict counts and the parcels that need a person's look.</summary>
    public const string Tranche = "tranche";

    /// <summary>One farm's risk for one season (Company, Insurance).</summary>
    public const string FarmRisk = "farm_risk";

    /// <summary>A farmer's own farms with their results for one season.</summary>
    public const string FarmerResults = "farmer_results";

    public static readonly IReadOnlyList<string> All = [Tranche, FarmRisk, FarmerResults];
}

public sealed class ReportFileInfo
{
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// A report as it was at freeze time (hard rules 4 and 5): the language-neutral snapshot, its hash, and the hashes of every file made from it.
/// Written once by the worker. A change is a new report with a new hash, never an edit.
/// </summary>
public sealed class FrozenReport : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Season { get; set; }
    public string? SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public string SnapshotSha256 { get; set; } = string.Empty;
    public string MethodVersions { get; set; } = string.Empty;
    public bool Synthetic { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<ReportFileInfo> Files { get; set; } = [];
    public string ManifestSha256 { get; set; } = string.Empty;
}

/// <summary>One rendered file of a frozen report. Stored apart from the report so listing stays light.</summary>
public sealed class ReportFile : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = [];
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record ReportJobPayload(string ReportId, string Kind, string? SubjectId, string? Season, string RequestedBy);
