using System.Globalization;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Programmes;
using Zr3i.Kernel.Reports;
using Zr3i.Modules.Risk;

namespace Zr3i.Reports;

/// <summary>
/// Reads the organisation's own data and turns it into a <see cref="ReportSnapshot"/>. It reuses the same services the screens use, so a report
/// can never show a number the screen would not. It never sorts or ranks by risk value.
/// </summary>
public sealed class SnapshotBuilder(KernelStore store, TrancheService tranches, RiskService risk, TimeProvider clock)
{
    public const int MaxRows = 500;
    public const int MaxFarms = 200;

    private static string N(double? v, int digits) => v is null ? string.Empty : Math.Round(v.Value, digits, MidpointRounding.AwayFromZero).ToString("0." + new string('0', digits), CultureInfo.InvariantCulture);

    public async Task<ReportSnapshot> BuildAsync(TenantScope scope, ReportJobPayload p, CancellationToken ct)
    {
        var org = (await store.Organisations.FindAsync(scope, o => o.Id == scope.TenantId, 1, ct)).FirstOrDefault();
        var name = org?.Name ?? string.Empty;
        return p.Kind switch
        {
            ReportKinds.Tranche => await TrancheAsync(scope, name, p, ct),
            ReportKinds.FarmRisk => await FarmRiskAsync(scope, name, p, ct),
            ReportKinds.FarmerResults => await FarmerResultsAsync(scope, name, p, ct),
            _ => throw new ValidationFailedException("unknown_kind"),
        };
    }

    private async Task<ReportSnapshot> TrancheAsync(TenantScope scope, string org, ReportJobPayload p, CancellationToken ct)
    {
        var check = await tranches.CheckAsync(scope, p.SubjectId ?? string.Empty, p.Season, ct);
        var facts = new List<SnapshotFact>
        {
            new("tranche", check.Tranche.Name),
            new("scheduled", check.Tranche.ScheduledDate),
            new("parcels", check.Tranche.ParcelIds.Count.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var v in Verdicts.All)
            facts.Add(new(v, (check.Verdicts.TryGetValue(v, out var n) ? n : 0).ToString(CultureInfo.InvariantCulture)));
        facts.Add(new("needsALook", check.NeedsALookTotal.ToString(CultureInfo.InvariantCulture)));

        var rows = check.NeedsALook.Select(i => (IReadOnlyList<string>)
        [
            i.Label, i.FarmName, i.Finding?.Status ?? Verdicts.None, N(i.MeasuredAreaFeddan, 2), N(i.DeclaredAreaFeddan, 2), N(i.Finding?.GapPercent, 0),
        ]).ToList();

        return new ReportSnapshot(ReportKinds.Tranche, p.Season ?? check.Tranche.Season, org, check.Tranche.Name, clock.GetUtcNow().UtcDateTime, null, null, null, check.Accuracy,
            false, facts, ["parcel", "farm", "verdict", "measured", "declared", "gap"], rows, check.NeedsALookTotal);
    }

    private async Task<ReportSnapshot> FarmRiskAsync(TenantScope scope, string org, ReportJobPayload p, CancellationToken ct)
    {
        var s = await risk.FarmSummaryAsync(scope, p.SubjectId ?? string.Empty, p.Season ?? string.Empty, ct);
        var facts = new List<SnapshotFact>
        {
            new("farm", s.Farm.Name),
            new("governorate", s.Farm.Governorate),
            new("fri", N(s.Fri?.Result.Value, 1)),
            new("awcri", N(s.Awcri, 1)),
            new("adjustments", N(s.AdjustmentsSum, 1)),
            new("parcels", s.ParcelCount.ToString(CultureInfo.InvariantCulture)),
            new("totalArea", N(s.TotalAreaFeddan, 2)),
        };
        var rows = s.Parcels.Select(r => (IReadOnlyList<string>)[r.Label, r.Crop ?? string.Empty, N(r.AreaFeddan, 2), N(r.Cri, 1), r.Status, r.Band?.Band ?? "no_band"]).ToList();

        return new ReportSnapshot(ReportKinds.FarmRisk, s.Season, org, s.Farm.Name, clock.GetUtcNow().UtcDateTime, FriCalculator.MethodId, FriCalculator.MethodVersion.ToString(CultureInfo.InvariantCulture),
            "specified", "NOT_ESTABLISHED", false, facts, ["parcel", "crop", "area", "cri", "status", "band"], rows, rows.Count);
    }

    /// <summary>A farmer's own farms for one season: one row per farm. The farmer reads their own data, so no consent is involved.</summary>
    private async Task<ReportSnapshot> FarmerResultsAsync(TenantScope scope, string org, ReportJobPayload p, CancellationToken ct)
    {
        var farms = (await store.Farms.FindAsync(scope, _ => true, MaxFarms, ct)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var f in farms)
        {
            var s = await risk.FarmSummaryAsync(scope, f.Id, p.Season ?? string.Empty, ct);
            rows.Add([f.Name, s.ParcelCount.ToString(CultureInfo.InvariantCulture), N(s.TotalAreaFeddan, 2), N(s.Awcri, 1), N(s.Fri?.Result.Value, 1)]);
        }

        var facts = new List<SnapshotFact> { new("farms", farms.Count.ToString(CultureInfo.InvariantCulture)) };
        return new ReportSnapshot(ReportKinds.FarmerResults, p.Season, org, org, clock.GetUtcNow().UtcDateTime, FriCalculator.MethodId, FriCalculator.MethodVersion.ToString(CultureInfo.InvariantCulture),
            "specified", "NOT_ESTABLISHED", false, facts, ["farm", "parcels", "area", "awcri", "fri"], rows, rows.Count);
    }
}
