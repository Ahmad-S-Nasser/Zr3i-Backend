using Zr3i.Kernel.Crops;
using Zr3i.Modules.Verification;
using Xunit;

namespace Zr3i.Tests.Unit.Verification;

public class CropEvidenceTests
{
    private static readonly DateOnly Sowing = new(2026, 10, 1);

    private static CropSettings Beet(int? separation = 20, int? cycle = 195) => new()
    {
        CycleDays = cycle, PassMinSeparationDays = separation,
        NdviThresholds = new() { ["greenup_start_below"] = 0.25, ["greenup_pass_above"] = 0.5, ["hold_above"] = 0.6, ["sawtooth_drop"] = 0.25 },
    };

    private static SceneNdvi At(int dayAfterSowing, double ndvi) => new(Sowing.AddDays(dayAfterSowing), ndvi);

    private static CropAssessment Run(IEnumerable<SceneNdvi> scenes, CropSettings? rule = null, bool belowResolution = false, string? crop = "beet", DateOnly? sowing = null, bool noRule = false) =>
        CropEvidence.Evaluate(new CropAssessmentInput(belowResolution, crop, sowing ?? Sowing, noRule ? null : rule ?? Beet(), 3, 2, 60, [.. scenes]));

    private static void Is(CropAssessment a, string type, string status, string? reason) => Assert.Equal((type, status, reason), (a.Type, a.Status, a.Reason));

    [Fact]
    public void AParcelTooSmallToTestIsNeverNoCropHoweverBareItsPixelsLook()
    {
        var bare = new[] { At(10, 0.12), At(70, 0.13), At(100, 0.14), At(130, 0.12) };
        Is(Run(bare, belowResolution: true), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.BelowResolution);
    }

    [Fact]
    public void ADeclarationWithoutACropOrASowingDateCannotBeAssessed()
    {
        var curve = new[] { At(10, 0.12), At(50, 0.4), At(80, 0.65) };
        Is(Run(curve, crop: null), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.MissingDeclaration);
        Is(CropEvidence.Evaluate(new CropAssessmentInput(false, "beet", null, Beet(), 3, 2, 60, [.. curve])), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.MissingDeclaration);
    }

    [Fact]
    public void ACropWithoutARuleOrWithAnIncompleteOneHasNoFindingBeyondSayingSo()
    {
        var curve = new[] { At(10, 0.12), At(50, 0.4), At(80, 0.65) };
        Is(Run(curve, noRule: true), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.NoRuleForCrop);
        Is(Run(curve, new CropSettings { CycleDays = 195 }), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.RuleIncomplete);
        Is(Run(curve, Beet(cycle: null)), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.RuleIncomplete);
    }

    [Fact]
    public void ThreeReadableScenesInsideTheSeasonAreNeededAndSceneOutsideItDoNotCount()
    {
        var two = new[] { At(10, 0.12), At(50, 0.4), At(196, 0.8), At(-1, 0.1) };                 // one after the cycle, one before sowing
        Is(Run(two), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.InsufficientScenes);
        var three = new[] { At(10, 0.12), At(50, 0.4), At(195, 0.8), At(-1, 0.1) };                // day 195 is inside, day -1 is not
        Assert.Equal(3, Run(three).ScenesUsed);
    }

    [Fact]
    public void ACurveThatGreensUpHoldsAndHasNoSawToothIsStatedAsCropPresentNotAsAVerdict()
    {
        var a = Run([At(10, 0.12), At(50, 0.4), At(80, 0.65), At(120, 0.8), At(160, 0.78)]);
        Is(a, FindingTypes.CropPresent, FindingStatuses.Measured, null);
        Assert.Equal(5, a.ScenesUsed);
        Assert.Equal(0.8, a.MaxNdvi);
        Assert.Equal(["greenup", "hold", "no_sawtooth"], a.RulesTested);                          // the clean end is not tested and says so by omission
    }

    [Fact]
    public void ACurveThatStartsGreenFailsGreenUpAndIsNotAssessableNotBare()
    {
        Is(Run([At(10, 0.7), At(50, 0.8), At(90, 0.8)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
    }

    [Fact]
    public void ASawToothThatDropsAndRecoversIsABerseemCutNotBeet()
    {
        Is(Run([At(10, 0.12), At(40, 0.45), At(70, 0.7), At(100, 0.35), At(130, 0.72), At(160, 0.75)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
    }

    [Fact]
    public void ADropOfExactlyTheLimitIsNotASawToothButMoreIs()
    {
        // 0.8 -> 0.55 is a drop of exactly 0.25 (not more), then recovery: allowed. 0.8 -> 0.54 is more than 0.25: a saw-tooth.
        var ok = Run([At(10, 0.12), At(40, 0.5), At(70, 0.8), At(100, 0.55), At(130, 0.8), At(160, 0.75)]);
        Is(ok, FindingTypes.CropPresent, FindingStatuses.Measured, null);
        // 0.55 - 0.30 is 0.25000000000000006 in floating point; it is still a drop of exactly the limit, so not a saw-tooth.
        var rounding = Run([At(10, 0.12), At(40, 0.5), At(70, 0.55), At(100, 0.30), At(130, 0.55), At(160, 0.6), At(190, 0.6)]);
        Is(rounding, FindingTypes.CropPresent, FindingStatuses.Measured, null);
        var bad = Run([At(10, 0.12), At(40, 0.5), At(70, 0.8), At(100, 0.54), At(130, 0.8), At(160, 0.75)]);
        Is(bad, FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
    }

    [Fact]
    public void ACurveThatNeverHoldsAboveTheHoldLevelForTwoScenesIsNotConsistent()
    {
        Is(Run([At(10, 0.12), At(50, 0.5), At(80, 0.55), At(110, 0.5)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
        Is(Run([At(10, 0.12), At(50, 0.5), At(80, 0.6), At(110, 0.5)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);   // only one scene reaches 0.6
        Is(Run([At(10, 0.12), At(50, 0.5), At(80, 0.6), At(110, 0.6)]), FindingTypes.CropPresent, FindingStatuses.Measured, null);                                          // two scenes at 0.6 hold
    }

    [Fact]
    public void ThresholdsAreInclusiveForReachingAndStrictForStayingBelow()
    {
        // Green-up needs the first scene strictly below 0.25 and a later one at or above 0.5.
        Is(Run([At(10, 0.25), At(50, 0.5), At(80, 0.7), At(110, 0.7)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
        Is(Run([At(10, 0.249), At(50, 0.5), At(80, 0.7), At(110, 0.7)]), FindingTypes.CropPresent, FindingStatuses.Measured, null);
        Is(Run([At(10, 0.12), At(50, 0.499), At(80, 0.55), At(110, 0.58)]), FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
    }

    [Fact]
    public void BareScenesSixtyDaysAfterSowingAreFlaggedOnlyOnTwoPassesFarEnoughApart()
    {
        // Scenes on days 70, 100 and 130 are bare and 30 days apart each (20 needed): three independent passes; day 10 is before the evidence window.
        var flagged = Run([At(10, 0.12), At(70, 0.13), At(100, 0.15), At(130, 0.14)]);
        Is(flagged, FindingTypes.NoCrop, FindingStatuses.Flagged, null);
        Assert.Equal(3, flagged.PassesUsed);
    }

    [Fact]
    public void WithoutAConfiguredSeparationABareFieldIsPendingNeverFlagged()
    {
        Is(Run([At(10, 0.12), At(70, 0.13), At(100, 0.15), At(130, 0.14)], Beet(separation: null)), FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.PassSeparationNotConfigured);
    }

    [Fact]
    public void BareScenesCloserThanTheSeparationAreOnePassSoTheFindingIsPending()
    {
        Is(Run([At(10, 0.12), At(62, 0.13), At(70, 0.14)]), FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.SecondPassNeeded);        // 8 days apart, 20 needed
        Is(Run([At(10, 0.12), At(62, 0.13), At(82, 0.14)]), FindingTypes.NoCrop, FindingStatuses.Flagged, null);                                      // exactly 20 days apart is enough
        Is(Run([At(10, 0.12), At(62, 0.13), At(81, 0.14)]), FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.SecondPassNeeded);          // 19 is not
    }

    [Fact]
    public void BeforeDaySixtyAFlatFieldIsStillTooEarlyToTellAndAtDaySixtyItCounts()
    {
        Is(Run([At(10, 0.12), At(40, 0.13), At(59, 0.14)]), FindingTypes.NotAssessable, FindingStatuses.Pending, FindingReasons.TooEarly);
        var atSixty = Run([At(10, 0.12), At(40, 0.13), At(60, 0.14)]);
        Is(atSixty, FindingTypes.NoCrop, FindingStatuses.Pending, FindingReasons.SecondPassNeeded);                                                  // day 60 is evidence, but one pass
    }

    [Fact]
    public void AFieldNotBareAfterSixtyDaysIsNeverNoCrop()
    {
        // 0.25 on day 70 is not "below 0.25", so the field shows some growth: not bare, and the curve is not beet-like either.
        var a = Run([At(10, 0.12), At(70, 0.25), At(100, 0.14), At(130, 0.14)]);
        Assert.NotEqual(FindingTypes.NoCrop, a.Type);
        Is(a, FindingTypes.NotAssessable, FindingStatuses.NotAssessable, FindingReasons.ConflictingEvidence);
    }

    [Fact]
    public void EvidenceNeverDependsOnTheOrderTheScenesArriveIn()
    {
        var curve = new[] { At(120, 0.8), At(10, 0.12), At(160, 0.78), At(80, 0.65), At(50, 0.4) };
        Is(Run(curve), FindingTypes.CropPresent, FindingStatuses.Measured, null);
        Is(Run(curve.Reverse()), FindingTypes.CropPresent, FindingStatuses.Measured, null);
    }
}
