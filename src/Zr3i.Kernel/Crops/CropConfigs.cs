using System.Globalization;
using System.Text.RegularExpressions;
using Zr3i.Data;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Methods;

namespace Zr3i.Kernel.Crops;

/// <summary>Where a crop's rule came from, so a finding can say so.</summary>
public static class CropRuleSources
{
    public const string CropConfig = "crop_config";
    public const string MethodStartingRule = "method_starting_rule";
}

/// <param name="ConfigId">The crop-configuration version used; null when the rule came from the method.</param>
/// <param name="Sha256">The version's seal, or the method's seal when it is frozen, otherwise null.</param>
public sealed record ResolvedCropRule(string CropCode, string Source, string? ConfigId, int? ConfigVersion, string? Sha256, CropSettings Settings);

/// <summary>
/// Per-crop settings kept apart from the method so an administrator can add or change a crop after launch without reopening a frozen
/// method. Versions are immutable; activating one needs a named approver and is audited. A crop with nothing active has no rule,
/// except beet, whose starting rules in the method are used and labelled as such until a configuration is activated.
/// </summary>
public sealed partial class CropConfigs(KernelStore store, AuditService audit, TimeProvider clock)
{
    private const string FallbackCrop = "beet";

    [GeneratedRegex("^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex CropShape();

    [GeneratedRegex(@"^(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\.\.(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$")]
    private static partial Regex WindowShape();

    /// <exception cref="ValidationFailedException">invalid_crop, reason_required, empty_settings, invalid:{field}, unknown_threshold:{key}, threshold_out_of_range:{key}.</exception>
    public async Task<CropConfigVersion> CreateVersionAsync(string actor, string cropCode, CropSettings settings, string reason, CancellationToken ct = default)
    {
        var errors = Validate(cropCode, settings, reason);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var existing = await store.CropConfigVersions.FindAsync(PlatformTenant.Scope, v => v.CropCode == cropCode, ct: ct);
        var version = new CropConfigVersion
        {
            CropCode = cropCode, Version = existing.Count == 0 ? 1 : existing.Max(v => v.Version) + 1, Settings = settings, Reason = reason.Trim(),
            CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = actor,
        };
        version.Sha256 = version.ComputeSeal();
        await store.CropConfigVersions.InsertAsync(PlatformTenant.Scope, version, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actor, "cropconfig.created", "crop_config", version.Id, purpose: $"{cropCode} v{version.Version}: {version.Reason}", ct: ct);
        return version;
    }

    /// <exception cref="ValidationFailedException">approver_required.</exception>
    public async Task<CropConfigActivation> ActivateAsync(string actor, string cropCode, int version, string approvedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ValidationFailedException("approver_required");
        var v = (await store.CropConfigVersions.FindAsync(PlatformTenant.Scope, x => x.CropCode == cropCode && x.Version == version, 1, ct)).FirstOrDefault()
            ?? throw new NotFoundException("crop_config", $"{cropCode} v{version}");
        var last = (await store.CropConfigActivations.FindAsync(PlatformTenant.Scope, a => a.CropCode == cropCode, ct: ct)).Select(a => a.Sequence).DefaultIfEmpty(0).Max();

        var activation = new CropConfigActivation
        {
            CropCode = cropCode, Version = v.Version, ApprovedBy = approvedBy.Trim(), ActivatedBy = actor,
            ActivatedAt = clock.GetUtcNow().UtcDateTime, Sequence = last + 1,
        };
        await store.CropConfigActivations.InsertAsync(PlatformTenant.Scope, activation, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actor, "cropconfig.activated", "crop_config", v.Id, purpose: $"{cropCode} v{version}, approved by {activation.ApprovedBy}", ct: ct);
        return activation;
    }

    public async Task<IReadOnlyList<CropConfigVersion>> VersionsAsync(string cropCode, CancellationToken ct = default) =>
        [.. (await store.CropConfigVersions.FindAsync(PlatformTenant.Scope, v => v.CropCode == cropCode, ct: ct)).OrderBy(v => v.Version)];

    public async Task<IReadOnlyList<(string CropCode, int? ActiveVersion)>> CropsAsync(CancellationToken ct = default)
    {
        var codes = (await store.CropConfigVersions.FindAsync(PlatformTenant.Scope, _ => true, ct: ct)).Select(v => v.CropCode).Distinct().Order(StringComparer.Ordinal).ToList();
        var result = new List<(string, int?)>();
        foreach (var code in codes) result.Add((code, (await ActiveAsync(code, ct))?.Version));
        return result;
    }

    /// <summary>The version in force: the one named by the latest activation.</summary>
    public async Task<CropConfigVersion?> ActiveAsync(string cropCode, CancellationToken ct = default)
    {
        var latest = (await store.CropConfigActivations.FindAsync(PlatformTenant.Scope, a => a.CropCode == cropCode, ct: ct)).OrderBy(a => a.Sequence).LastOrDefault();
        if (latest is null) return null;
        return (await store.CropConfigVersions.FindAsync(PlatformTenant.Scope, v => v.CropCode == cropCode && v.Version == latest.Version, 1, ct)).FirstOrDefault();
    }

    /// <summary>The rule to use for a crop, or null when there is none ("no rule for this crop"). Never a guess.</summary>
    public async Task<ResolvedCropRule?> ResolveAsync(string cropCode, MethodVersion method, CancellationToken ct = default)
    {
        if (await ActiveAsync(cropCode, ct) is { } active)
            return new ResolvedCropRule(cropCode, CropRuleSources.CropConfig, active.Id, active.Version, active.Sha256, active.Settings);
        if (cropCode != FallbackCrop) return null;

        var s = new CropSettings { CycleDays = IntOrNull(method.Get("crop.beet.cycle_days")), SowingWindow = method.Get("crop.beet.sowing_window") };
        foreach (var key in CropConfigRules.NdviThresholdKeys)
            if (double.TryParse(method.Get("crop.beet." + key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) s.NdviThresholds[key] = d;
        return new ResolvedCropRule(cropCode, CropRuleSources.MethodStartingRule, null, null, method.ParametersSha256, s);
    }

    private static int? IntOrNull(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static List<string> Validate(string cropCode, CropSettings s, string reason)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(cropCode) || !CropShape().IsMatch(cropCode)) errors.Add("invalid_crop");
        if (string.IsNullOrWhiteSpace(reason)) errors.Add("reason_required");
        if (s.IsEmpty) errors.Add("empty_settings");
        if (s.EmergenceToleranceDays is < 0 or > 120) errors.Add("invalid:emergence_tolerance_days");
        if (s.PassMinSeparationDays is < 1 or > 120) errors.Add("invalid:pass_min_separation_days");
        if (s.CycleDays is < 30 or > 400) errors.Add("invalid:cycle_days");
        if (s.SowingWindow is not null && !WindowShape().IsMatch(s.SowingWindow)) errors.Add("invalid:sowing_window");
        foreach (var (key, value) in s.NdviThresholds)
        {
            if (!CropConfigRules.NdviThresholdKeys.Contains(key)) errors.Add("unknown_threshold:" + key);
            else if (double.IsNaN(value) || value is < 0 or > 1) errors.Add("threshold_out_of_range:" + key);
        }

        return errors;
    }
}
