using Zr3i.Data;

namespace Zr3i.Kernel.Findings;

/// <summary>One stratum of the register: the group a finding's <c>AccuracyRef</c> names.</summary>
public sealed record AccuracyEntry(
    string AccuracyRef, string MethodId, int MethodVersion, string FindingType, string Crop, string SizeClass,
    int CurrentFindings, IReadOnlyDictionary<string, int> ByStatus, string AccuracyState, double? AccuracyValue);

/// <summary>
/// The accuracy register: for each stratum, how many current findings the platform has made. Accuracy itself is "not_established"
/// with no value until checks against known-correct answers are loaded; this never states an accuracy that was not measured.
/// </summary>
public sealed class AccuracyRegister(KernelStore store)
{
    public const string NotEstablished = "not_established";

    public async Task<IReadOnlyList<AccuracyEntry>> ListAsync(TenantScope scope, CancellationToken ct = default)
    {
        var all = new List<Finding>();
        string? cursor = null;
        do
        {
            var page = await store.Findings.FindPageAsync(scope, _ => true, cursor, 1000, ct);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var superseded = all.Where(f => f.Supersedes != null).Select(f => f.Supersedes!).ToHashSet();
        return [.. all.Where(f => !superseded.Contains(f.Id))
            .GroupBy(f => f.AccuracyRef, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var parts = g.Key.Split('/');
                return new AccuracyEntry(
                    g.Key, first.MethodId, first.MethodVersion, first.Type, parts.Length == 5 ? parts[3] : "-", parts.Length == 5 ? parts[4] : "unclassified",
                    g.Count(), g.GroupBy(f => f.Status).OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Count()), NotEstablished, null);
            })];
    }
}
