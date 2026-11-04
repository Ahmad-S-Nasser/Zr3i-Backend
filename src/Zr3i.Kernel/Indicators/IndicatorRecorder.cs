using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zr3i.Data;

namespace Zr3i.Kernel.Indicators;

public sealed record IndicatorDraft(
    string Indicator, string SubjectType, string SubjectId, string MethodId, int MethodVersion, IReadOnlyList<IndicatorComponent> Components, double Value,
    double? ValidFraction = null, bool Insufficient = false, IReadOnlyList<IndicatorAdjustment>? Adjustments = null, int? ClosedSeasons = null);

/// <summary>Writes indicator results under the immutability rules. It does no scoring: the caller supplies the components and the value.</summary>
public sealed class IndicatorRecorder(KernelStore store, TimeProvider clock)
{
    private static readonly string[] SubjectTypes = ["parcel_season", "farm_season"];

    /// <exception cref="ValidationFailedException">Any code from <see cref="Validate"/>, or value_differs_for_same_inputs.</exception>
    public async Task<IndicatorResult> RecordAsync(TenantScope scope, string actorUserId, IndicatorDraft draft, CancellationToken ct = default)
    {
        Validate(draft);
        var hash = HashOf(draft);
        var latest = await LatestAsync(scope, draft, ct);
        if (latest is not null && latest.InputSha256 == hash) return Same(latest, draft);

        var result = new IndicatorResult
        {
            Indicator = draft.Indicator, SubjectType = draft.SubjectType, SubjectId = draft.SubjectId, MethodId = draft.MethodId, MethodVersion = draft.MethodVersion,
            InputSha256 = hash, Value = draft.Value, Components = [.. draft.Components.OrderBy(c => c.Key, StringComparer.Ordinal)],
            MissingComponents = [.. draft.Components.Where(c => !c.Established).Select(c => c.Key).Order(StringComparer.Ordinal)],
            ValidFraction = draft.ValidFraction, Insufficient = draft.Insufficient, ClosedSeasons = draft.ClosedSeasons,
            Adjustments = [.. (draft.Adjustments ?? []).OrderBy(a => a.Key, StringComparer.Ordinal)],
            Maturity = IndicatorMaturity.Specified, Supersedes = latest?.Id, CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = actorUserId,
        };
        try
        {
            await store.IndicatorResults.InsertAsync(scope, result, ct);
            return result;
        }
        catch (DuplicateKeyException)
        {
            // Another writer recorded the same inputs first; return that one.
            var again = await LatestAsync(scope, draft, ct);
            if (again is not null && again.InputSha256 == hash) return Same(again, draft);
            throw;
        }
    }

    private static IndicatorResult Same(IndicatorResult existing, IndicatorDraft draft) =>
        existing.Value.Equals(draft.Value) ? existing : throw new ValidationFailedException("value_differs_for_same_inputs");

    private async Task<IndicatorResult?> LatestAsync(TenantScope scope, IndicatorDraft d, CancellationToken ct)
    {
        var rows = new List<IndicatorResult>();
        string? cursor = null;
        do
        {
            var page = await store.IndicatorResults.FindPageAsync(scope, r => r.Indicator == d.Indicator && r.SubjectType == d.SubjectType && r.SubjectId == d.SubjectId, cursor, 1000, ct);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var superseded = rows.Where(r => r.Supersedes != null).Select(r => r.Supersedes!).ToHashSet();
        return rows.Where(r => !superseded.Contains(r.Id)).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).LastOrDefault();
    }

    public static void Validate(IndicatorDraft d)
    {
        var errors = new List<string>();
        if (d.Indicator is not (IndicatorKinds.Cri or IndicatorKinds.Fri)) errors.Add("invalid_indicator");
        if (!SubjectTypes.Contains(d.SubjectType)) errors.Add("invalid_subject_type");
        if (string.IsNullOrWhiteSpace(d.SubjectId)) errors.Add("invalid_subject");
        if (string.IsNullOrWhiteSpace(d.MethodId) || d.MethodVersion < 1) errors.Add("invalid_method");
        if (!double.IsFinite(d.Value)) errors.Add("invalid_value");
        if (d.ValidFraction is { } vf && (!double.IsFinite(vf) || vf < 0 || vf > 1)) errors.Add("invalid_valid_fraction");
        if (d.Adjustments is { } adj)
        {
            if (adj.Any(a => string.IsNullOrWhiteSpace(a.Key) || (a.Value is { } av && !double.IsFinite(av)))) errors.Add("invalid_adjustment");
            if (adj.GroupBy(a => a.Key, StringComparer.Ordinal).Any(g => g.Count() > 1)) errors.Add("duplicate_adjustment");
        }
        if (d.Components.Count == 0) errors.Add("no_components");
        if (d.Components.Any(c => string.IsNullOrWhiteSpace(c.Key) || !double.IsFinite(c.Weight) || c.Weight <= 0)) errors.Add("invalid_weight");
        if (d.Components.GroupBy(c => c.Key, StringComparer.Ordinal).Any(g => g.Count() > 1)) errors.Add("duplicate_component");
        if (d.Components.Any(c => c.Established && (c.Score is null || !double.IsFinite(c.Score.Value)))) errors.Add("score_missing_for_established");
        if (d.Components.Any(c => !c.Established && c.Score is not null)) errors.Add("score_present_for_not_established");
        if (errors.Count > 0) throw new ValidationFailedException(errors.Distinct().ToList());
    }

    /// <summary>Hash of everything that decides the result, written in a fixed form so it cannot vary between runs. The value is derived, so it is not part of the hash.</summary>
    public static string HashOf(IndicatorDraft d)
    {
        static string N(double? v) => v is null ? "-" : v.Value.ToString("R", CultureInfo.InvariantCulture);
        var parts = d.Components.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={N(c.Weight)}:{N(c.Score)}:{(c.Established ? 1 : 0)}:{c.Feature ?? "-"}:{N(c.FeatureValue)}");
        var text = string.Join('|', d.Indicator, d.SubjectType, d.SubjectId, d.MethodId, d.MethodVersion, string.Join(';', parts), N(d.ValidFraction), d.Insufficient ? 1 : 0, d.ClosedSeasons?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + (d.Adjustments is null ? string.Empty : "|adj:" + string.Join(';', d.Adjustments.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + N(a.Value))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
