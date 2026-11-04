using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Indicators;

namespace Zr3i.Modules.Risk;

/// <summary>Computes a parcel-season's CRI and records it as an immutable indicator result. Maturity is always "specified".</summary>
public sealed class CriService(IndicatorRecorder recorder)
{
    /// <exception cref="ValidationFailedException">invalid_valid_fraction, no_established_components, or any recorder code.</exception>
    public Task<IndicatorResult> RecordAsync(TenantScope scope, string actorUserId, string parcelSeasonId, CriFeatures features, CancellationToken ct = default)
    {
        if (!double.IsFinite(features.ValidFraction) || features.ValidFraction is < 0 or > 1) throw new ValidationFailedException("invalid_valid_fraction");
        var cri = CriCalculator.Compute(features);
        if (cri.Value is not { } value) throw new ValidationFailedException("no_established_components");

        return recorder.RecordAsync(scope, actorUserId, new IndicatorDraft(
            IndicatorKinds.Cri, "parcel_season", parcelSeasonId, CriCalculator.MethodId, CriCalculator.MethodVersion, cri.Components, value, cri.ValidFraction, cri.Insufficient), ct);
    }
}
