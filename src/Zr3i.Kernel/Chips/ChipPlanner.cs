using System.Security.Cryptography;
using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Kernel.Chips;

public sealed record ChipRequest(string? JobId, bool AlreadyMade);

/// <summary>
/// Queues the making of the pictures for one parcel and one scene. Nothing is read or drawn in the request (rule 6): the worker does it.
/// A parcel too small to test, or a scene that lost too much of it, gets no picture; the screen says why instead of showing an empty one.
/// </summary>
public sealed class ChipPlanner(KernelStore store, JobQueue jobs, AuditService audit, TimeProvider clock)
{
    public const string JobType = "imagery.chip";

    /// <exception cref="NotFoundException">parcel, scene.</exception>
    /// <exception cref="ValidationFailedException">no_boundary, not_observed, below_resolution, not_readable.</exception>
    public async Task<ChipRequest> PlanAsync(TenantScope scope, string actorUserId, string parcelId, string? sceneId, CancellationToken ct = default)
    {
        var parcel = await store.Parcels.GetAsync(scope, parcelId, ct) ?? throw new NotFoundException("parcel", parcelId);
        if (string.IsNullOrWhiteSpace(sceneId)) throw new ValidationFailedException("scene_required");
        if (parcel.CurrentBoundaryId is null) throw new ValidationFailedException("no_boundary");
        if ((await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SceneId == sceneId, 1, ct)).Count == 0) throw new NotFoundException("scene", sceneId);

        var observation = (await store.ParcelObservations.FindAsync(scope, o => o.ParcelId == parcelId && o.SceneId == sceneId && o.BoundaryId == parcel.CurrentBoundaryId, 1, ct)).FirstOrDefault()
            ?? throw new ValidationFailedException("not_observed");
        if (observation.Reason == ObservationReasons.BelowResolution) throw new ValidationFailedException("below_resolution");
        if (!observation.Readable) throw new ValidationFailedException("not_readable");

        var made = await store.ParcelChips.FindAsync(scope, c => c.ParcelId == parcelId && c.SceneId == sceneId && c.BoundaryId == parcel.CurrentBoundaryId, 10, ct);
        if (ChipKinds.All.All(k => made.Any(c => c.Kind == k))) return new ChipRequest(null, true);

        var job = await jobs.EnqueueAsync(scope, JobType, JobJson.Serialize(new ChipPayload(parcelId, sceneId)), actorUserId, clock.GetUtcNow().UtcDateTime, ct: ct);
        await audit.RecordAsync(scope, actorUserId, "imagery.chip_requested", "parcel", parcelId, purpose: sceneId, ct: ct);
        return new ChipRequest(job.Id, false);
    }
}

/// <summary>Lists a parcel's pictures and returns one. A file is checked against the hash on its row each time it is read.</summary>
public sealed class ChipService(KernelStore store, IChipStore files)
{
    /// <exception cref="NotFoundException">parcel.</exception>
    public async Task<IReadOnlyList<ParcelChip>> ListAsync(TenantScope scope, string parcelId, CancellationToken ct = default)
    {
        _ = await store.Parcels.GetAsync(scope, parcelId, ct) ?? throw new NotFoundException("parcel", parcelId);
        return [.. (await store.ParcelChips.FindAsync(scope, c => c.ParcelId == parcelId, 500, ct)).OrderBy(c => c.SensingTime).ThenBy(c => c.SceneId, StringComparer.Ordinal).ThenBy(c => c.Kind, StringComparer.Ordinal)];
    }

    /// <exception cref="NotFoundException">parcel, chip.</exception>
    /// <exception cref="ValidationFailedException">chip_corrupt: the file is missing or does not match the hash stored with it.</exception>
    public async Task<(ParcelChip Chip, byte[] Data)> ReadAsync(TenantScope scope, string parcelId, string chipId, CancellationToken ct = default)
    {
        _ = await store.Parcels.GetAsync(scope, parcelId, ct) ?? throw new NotFoundException("parcel", parcelId);
        var chip = await store.ParcelChips.GetAsync(scope, chipId, ct);
        if (chip is null || chip.ParcelId != parcelId) throw new NotFoundException("chip", chipId);

        var data = await files.ReadAsync(chip.Path, ct);
        if (data is null || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), chip.Sha256, StringComparison.OrdinalIgnoreCase)) throw new ValidationFailedException("chip_corrupt");
        return (chip, data);
    }
}
