using Zr3i.Kernel.Crops;

namespace Zr3i.Modules.Verification;

public static class FindingTypes
{
    public const string AreaGap = "area_gap";
    public const string CropPresent = "crop_present";
    public const string NoCrop = "no_crop";
    public const string NotAssessable = "not_assessable";
}

/// <summary>
/// What a finding says about itself. None of these is a decision: the platform states a fact, a person decides.
/// MEASURED: a value or an observation is stated. FLAGGED: beyond the field's own noise, or backed by two independent passes.
/// PENDING: waiting for more evidence. NOT_ASSESSABLE: the evidence is not enough to say anything.
/// </summary>
public static class FindingStatuses
{
    public const string Measured = "MEASURED";
    public const string Flagged = "FLAGGED";
    public const string Pending = "PENDING";
    public const string NotAssessable = "NOT_ASSESSABLE";
}

/// <summary>Stable reason codes; the screens turn them into English and Arabic words.</summary>
public static class FindingReasons
{
    public const string BelowResolution = "below_resolution";
    public const string MissingDeclaration = "missing_declaration";
    public const string NoBoundary = "no_boundary";
    public const string BoundaryIncomplete = "boundary_incomplete";
    public const string NoRuleForCrop = "no_rule_for_crop";
    public const string RuleIncomplete = "rule_incomplete";
    public const string InsufficientScenes = "insufficient_scenes";
    public const string ConflictingEvidence = "conflicting_evidence";
    public const string PassSeparationNotConfigured = "pass_separation_not_configured";
    public const string SecondPassNeeded = "second_pass_needed";
    public const string TooEarly = "too_early_to_tell";
}

/// <summary>One readable scene's NDVI (median over the parcel's usable whole pixels) on the day it was sensed.</summary>
public sealed record SceneNdvi(DateOnly Date, double Ndvi);

/// <param name="BelowResolution">The parcel has too few whole pixels to be tested at 10 m at all.</param>
/// <param name="Rule">Null when the crop has no rule.</param>
/// <param name="MinValidScenes">Readable scenes needed inside the season (crop.min_valid_scenes).</param>
/// <param name="MinIndependentPasses">Independent passes needed before an observation-derived flag (pass.min_independent).</param>
/// <param name="BareAfterSowingDays">Days after sowing from which a still-bare field is evidence of no crop (crop.no_crop.bare_after_sowing_days).</param>
public sealed record CropAssessmentInput(
    bool BelowResolution, string? DeclaredCrop, DateOnly? Sowing, CropSettings? Rule, int MinValidScenes, int MinIndependentPasses, int BareAfterSowingDays,
    IReadOnlyList<SceneNdvi> Scenes);

/// <param name="RulesTested">The parts of the crop's colour rule that were checked. What is absent was not checked.</param>
public sealed record CropAssessment(string Type, string Status, string? Reason, int ScenesUsed, int PassesUsed, double? MaxNdvi, IReadOnlyList<string> RulesTested);

/// <summary>
/// The crop-evidence rules of Rung 0 (Product Explained 3A.1), as a pure function. A consistency test against the crop's colour rule,
/// not a classifier: it can say a curve fits the declared crop, that a field stayed bare, or that the evidence is not enough.
/// A parcel too small to test is never "no crop". Missing evidence is never negative evidence.
/// </summary>
public static class CropEvidence
{
    private const double Epsilon = 1e-9;

    public static CropAssessment Evaluate(CropAssessmentInput input)
    {
        if (input.BelowResolution) return Stop(FindingReasons.BelowResolution);
        if (string.IsNullOrWhiteSpace(input.DeclaredCrop) || input.Sowing is null) return Stop(FindingReasons.MissingDeclaration);
        if (input.Rule is null) return Stop(FindingReasons.NoRuleForCrop);

        var t = input.Rule.NdviThresholds;
        if (input.Rule.CycleDays is not { } cycle
            || !t.TryGetValue("greenup_start_below", out var startBelow) || !t.TryGetValue("greenup_pass_above", out var passAbove)
            || !t.TryGetValue("hold_above", out var holdAbove) || !t.TryGetValue("sawtooth_drop", out var drop))
            return Stop(FindingReasons.RuleIncomplete);

        var sowing = input.Sowing.Value;
        var scenes = input.Scenes.Where(s => s.Date >= sowing && s.Date <= sowing.AddDays(cycle)).OrderBy(s => s.Date).ToList();
        var max = scenes.Count == 0 ? (double?)null : scenes.Max(s => s.Ndvi);
        if (scenes.Count < input.MinValidScenes) return new CropAssessment(FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.InsufficientScenes, scenes.Count, 0, max, []);

        int Day(SceneNdvi s) => s.Date.DayNumber - sowing.DayNumber;

        // A field still bare from day 60 on is the cleanest evidence of no crop; it takes two independent passes to flag it.
        var late = scenes.Where(s => Day(s) >= input.BareAfterSowingDays).ToList();
        if (late.Count > 0 && late.All(s => s.Ndvi < startBelow))
        {
            if (input.Rule.PassMinSeparationDays is not { } separation)
                return new CropAssessment(FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.PassSeparationNotConfigured, scenes.Count, 0, max, ["bare_after_sowing"]);
            var passes = IndependentPasses(late, separation);
            return passes >= input.MinIndependentPasses
                ? new CropAssessment(FindingTypes.NoCrop, FindingStatuses.Flagged, null, scenes.Count, passes, max, ["bare_after_sowing"])
                : new CropAssessment(FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.SecondPassNeeded, scenes.Count, passes, max, ["bare_after_sowing"]);
        }

        // Green-up: the first scene is below the start level and a later one reaches the pass level.
        var greenedUp = scenes[0].Ndvi < startBelow && scenes.Skip(1).Any(s => s.Ndvi >= passAbove);
        if (!greenedUp)
        {
            // Nothing can be said before the bare-field evidence window opens; afterwards a curve that does not fit is not enough evidence.
            return Day(scenes[^1]) < input.BareAfterSowingDays
                ? new CropAssessment(FindingTypes.NotAssessable, FindingStatuses.Pending, FindingReasons.TooEarly, scenes.Count, 0, max, [])
                : new CropAssessment(FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence, scenes.Count, 0, max, ["greenup"]);
        }

        var holds = scenes.Count(s => s.Ndvi >= holdAbove) >= 2;
        var sawTooth = HasSawTooth(scenes, drop);
        return holds && !sawTooth
            ? new CropAssessment(FindingTypes.CropPresent, FindingStatuses.Measured, null, scenes.Count, 0, max, ["greenup", "hold", "no_sawtooth"])
            : new CropAssessment(FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence, scenes.Count, 0, max, ["greenup", "hold", "no_sawtooth"]);
    }

    private static CropAssessment Stop(string reason) => new(FindingTypes.NotAssessable, FindingStatuses.NotAssessable, reason, 0, 0, null, []);

    /// <summary>Scenes taken earliest first, each at least <paramref name="separationDays"/> after the last one counted.</summary>
    private static int IndependentPasses(IReadOnlyList<SceneNdvi> ordered, int separationDays)
    {
        var count = 0;
        DateOnly? last = null;
        foreach (var s in ordered)
        {
            if (last is null || s.Date.DayNumber - last.Value.DayNumber >= separationDays)
            {
                count++;
                last = s.Date;
            }
        }

        return count;
    }

    /// <summary>A drop of more than the limit between two scenes that is later recovered by more than the limit: a cut, not a crop.</summary>
    private static bool HasSawTooth(IReadOnlyList<SceneNdvi> ordered, double limit)
    {
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            if (ordered[i].Ndvi - ordered[i + 1].Ndvi <= limit + Epsilon) continue;
            var trough = ordered[i + 1].Ndvi;
            for (var k = i + 2; k < ordered.Count; k++)
                if (ordered[k].Ndvi - trough > limit + Epsilon) return true;
        }

        return false;
    }
}
