using System.Security.Cryptography;
using System.Text;
using Zr3i.Data;

namespace Zr3i.Kernel.Crops;

/// <summary>
/// What the platform knows about one crop, set by a Zr3i administrator after launch. Every value is optional: an absent value means
/// "not configured", and the engine then makes no finding that needs it. Never a guessed default.
/// </summary>
public sealed class CropSettings
{
    /// <summary>Days either side of the declared sowing window before an emergence finding is raised.</summary>
    public int? EmergenceToleranceDays { get; set; }

    /// <summary>Minimum days between two passes before they count as independent for an in-season flag.</summary>
    public int? PassMinSeparationDays { get; set; }

    /// <summary>Month-day range such as "08-15..11-15" (may cross the new year).</summary>
    public string? SowingWindow { get; set; }

    public int? CycleDays { get; set; }

    /// <summary>NDVI colour rules by name (see <see cref="CropConfigRules.NdviThresholdKeys"/>), each between 0 and 1.</summary>
    public SortedDictionary<string, double> NdviThresholds { get; set; } = [];

    public bool IsEmpty => EmergenceToleranceDays is null && PassMinSeparationDays is null && SowingWindow is null && CycleDays is null && NdviThresholds.Count == 0;
}

/// <summary>One immutable version of a crop's settings, sealed with a SHA-256 so a finding can cite exactly what it used.</summary>
public sealed class CropConfigVersion : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string CropCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public CropSettings Settings { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;

    public string ComputeSeal()
    {
        var s = Settings;
        var thresholds = string.Join(',', s.NdviThresholds.Select(t => FormattableString.Invariant($"{t.Key}={t.Value:R}")));
        var text = FormattableString.Invariant($"{CropCode}|{Version}|{s.EmergenceToleranceDays}|{s.PassMinSeparationDays}|{s.SowingWindow}|{s.CycleDays}|{thresholds}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>An activation event. Versions never change; which one is in force is the latest activation, so a rollback is another activation.</summary>
public sealed class CropConfigActivation : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string CropCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public string ActivatedBy { get; set; } = string.Empty;
    public DateTime ActivatedAt { get; set; }
    public long Sequence { get; set; }
}

public static class CropConfigRules
{
    public static readonly IReadOnlyList<string> NdviThresholdKeys = ["greenup_start_below", "greenup_pass_above", "hold_above", "sawtooth_drop"];
}
