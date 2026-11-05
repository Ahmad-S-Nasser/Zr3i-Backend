using System.Globalization;
using Zr3i.Data;
using Zr3i.Kernel.Farms;

namespace Zr3i.Kernel.Findings;

public sealed record SceneMonth(string Month, int Scenes);

public sealed record VerificationAnalysis(string Season, int Parcels, IReadOnlyDictionary<string, int> Verdicts, IReadOnlyList<SceneMonth> ScenesPerMonth, string Accuracy);

/// <summary>
/// The counts behind the Data analysis screen: how many parcels have each verdict (by the current area finding), and how many distinct usable
/// scenes the organisation's parcels were read from each month. It reports counts and nothing else: no rate, no ranking, no score.
/// </summary>
public sealed class VerificationAnalysisService(KernelStore store)
{
    /// <exception cref="ValidationFailedException">invalid_season.</exception>
    public async Task<VerificationAnalysis> SummaryAsync(TenantScope scope, string season, CancellationToken ct = default)
    {
        var s = season?.Trim() ?? string.Empty;
        if (!FarmRules.IsValidSeason(s)) throw new ValidationFailedException("invalid_season");

        var parcels = (int)await store.Parcels.CountAsync(scope, _ => true, ct);
        var findings = new List<Finding>();
        string? cursor = null;
        do
        {
            var page = await store.Findings.FindPageAsync(scope, f => f.Season == s && f.Slot == FindingSlots.Area, cursor, 1000, ct);
            findings.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var superseded = findings.Where(f => f.Supersedes != null).Select(f => f.Supersedes!).ToHashSet();
        var current = findings.Where(f => !superseded.Contains(f.Id)).GroupBy(f => f.ParcelId).Select(g => g.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id, StringComparer.Ordinal).Last()).ToList();

        var verdicts = Verdicts.All.Where(v => v != Verdicts.None).ToDictionary(v => v, v => current.Count(f => f.Status == v));
        verdicts[Verdicts.None] = Math.Max(0, parcels - current.Count);

        var scenes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        cursor = null;
        do
        {
            var page = await store.ParcelObservations.FindPageAsync(scope, o => o.Readable, cursor, 1000, ct);
            foreach (var o in page.Items)
            {
                var month = o.SensingTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (!scenes.TryGetValue(month, out var set)) scenes[month] = set = [];
                set.Add(o.SceneId);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        // No walked and judged sample exists yet, so no hit rate is established (the accuracy register says the same).
        return new VerificationAnalysis(s, parcels, verdicts, [.. scenes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SceneMonth(kv.Key, kv.Value.Count))], "NOT_ESTABLISHED");
    }
}
