using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;

namespace Zr3i.Modules.Risk;

/// <summary>Bands A to E from a percentile (methodology 10.3). A pure function of the percentile, with half-open ranges so no value falls between bands.</summary>
public static class Bands
{
    /// <summary>A is [0, 20), B [20, 40), C [40, 60), D [60, 80), E [80, 100].</summary>
    public static string FromPercentile(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 100) throw new ArgumentOutOfRangeException(nameof(percentile), "A percentile is 0 to 100.");
        return percentile < 20 ? "A" : percentile < 40 ? "B" : percentile < 60 ? "C" : percentile < 80 ? "D" : "E";
    }

    /// <summary>
    /// The share of reference scores at or below the working score, as a percentage (the same reading the methodology gives its cohort
    /// percentile). A low working score is low risk and gives a low percentile, so band A is the best.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sortedScores, double workingScore)
    {
        if (sortedScores.Count == 0) throw new ArgumentException("The reference is empty.", nameof(sortedScores));
        int lo = 0, hi = sortedScores.Count;                                                              // first index with a score greater than the working score
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sortedScores[mid] <= workingScore) lo = mid + 1; else hi = mid;
        }

        return 100.0 * lo / sortedScores.Count;
    }
}

public sealed record CohortCandidate(string Level, string Key);

/// <summary>A banded working score and where the band came from, so the cohort and the reference version are always recorded with the value.</summary>
public sealed record BandAssessment(double Percentile, string Band, string CohortLevel, string CohortKey, int ReferenceVersion, int ReferenceSize, string ReferenceSha256);

/// <summary>Stores reference distributions (immutable, versioned) and bands working scores against them.</summary>
public sealed class ReferenceService(KernelStore store, TimeProvider clock)
{
    public const int MinSeasons = 5;
    public const int MinCohortSize = 30;
    public const int MaxScores = 300_000;

    /// <exception cref="ValidationFailedException">invalid_kind, invalid_cohort, too_few_seasons, no_scores, too_many_scores, invalid_score.</exception>
    public async Task<ReferenceDistribution> AddAsync(TenantScope scope, string actorUserId, string kind, string level, string cohortKey, IReadOnlyList<string> seasons, IReadOnlyList<double> scores, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (kind is not (ReferenceKinds.Parcel or ReferenceKinds.Farm)) errors.Add("invalid_kind");
        if (level is not (CohortLevels.CropGovernorate or CohortLevels.CropRegion or CohortLevels.CropNational) || string.IsNullOrWhiteSpace(cohortKey)) errors.Add("invalid_cohort");
        if (seasons.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Count() < MinSeasons) errors.Add("too_few_seasons");
        if (scores.Count == 0) errors.Add("no_scores");
        if (scores.Count > MaxScores) errors.Add("too_many_scores");
        if (scores.Any(s => !double.IsFinite(s))) errors.Add("invalid_score");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var sorted = scores.Order().ToList();
        var key = cohortKey.Trim();
        var previous = await LatestAsync(scope, kind, level, key, ct);
        var row = new ReferenceDistribution
        {
            Kind = kind, CohortLevel = level, CohortKey = key, Version = (previous?.Version ?? 0) + 1, Scores = sorted,
            Seasons = [.. seasons.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ScoresSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', sorted.Select(s => s.ToString("R", CultureInfo.InvariantCulture)))))),
            CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = actorUserId,
        };
        await store.ReferenceDistributions.InsertAsync(scope, row, ct);
        return row;
    }

    /// <summary>
    /// Bands a working score. Candidates run from the narrowest cohort to the widest; the first whose latest reference holds at least 30
    /// scores is used, otherwise the widest one that exists. Returns null when no candidate has a reference: there is then no band.
    /// </summary>
    public async Task<BandAssessment?> AssessAsync(TenantScope scope, string kind, IReadOnlyList<CohortCandidate> candidates, double workingScore, CancellationToken ct = default)
    {
        ReferenceDistribution? chosen = null;
        foreach (var c in candidates)
        {
            var reference = await LatestAsync(scope, kind, c.Level, c.Key, ct);
            if (reference is null) continue;
            chosen = reference;
            if (reference.Scores.Count >= MinCohortSize) break;
        }

        if (chosen is null) return null;
        var p = Bands.Percentile(chosen.Scores, workingScore);
        return new BandAssessment(p, Bands.FromPercentile(p), chosen.CohortLevel, chosen.CohortKey, chosen.Version, chosen.Scores.Count, chosen.ScoresSha256);
    }

    /// <summary>The version a value was banded against stays retrievable, so history can be reproduced.</summary>
    public async Task<ReferenceDistribution?> GetVersionAsync(TenantScope scope, string kind, string level, string cohortKey, int version, CancellationToken ct = default) =>
        (await store.ReferenceDistributions.FindAsync(scope, r => r.Kind == kind && r.CohortLevel == level && r.CohortKey == cohortKey && r.Version == version, 1, ct)).FirstOrDefault();

    private async Task<ReferenceDistribution?> LatestAsync(TenantScope scope, string kind, string level, string key, CancellationToken ct) =>
        (await store.ReferenceDistributions.FindAsync(scope, r => r.Kind == kind && r.CohortLevel == level && r.CohortKey == key, 1000, ct)).OrderBy(r => r.Version).LastOrDefault();
}
