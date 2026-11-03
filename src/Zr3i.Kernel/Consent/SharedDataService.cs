using Zr3i.Data;

namespace Zr3i.Kernel.Consent;

/// <summary>A parcel as a receiving organisation sees it: no owner identifiers, only what the consent covers.</summary>
public sealed record SharedParcel(string Id, string Label, double? AreaFeddan);

public sealed record SharedParcels(string GrantorName, string Purpose, DateTime ValidUntil, IReadOnlyList<SharedParcel> Parcels, int Withheld);

/// <summary>
/// The model for a cross-tenant read: open the gate, then read only inside it. A receiving organisation names the parcels it wants;
/// those the grant does not cover are returned only as a count.
/// </summary>
public sealed class SharedDataService(KernelStore store, ConsentGate gate)
{
    /// <exception cref="ConsentDeniedException">The gate refused; nothing was read.</exception>
    public async Task<SharedParcels> ReadParcelsAsync(TenantScope grantee, string actorUserId, string grantId, IReadOnlyList<string>? requested, CancellationToken ct = default)
    {
        var access = await gate.OpenAsync(grantee, actorUserId, grantId, ct);
        var (allowed, withheld) = access.Resolve(requested);
        var take = allowed.Take(1000).ToList();

        var parcels = take.Count == 0 ? [] : await store.Parcels.FindAsync(access.OwnerScope, p => take.Contains(p.Id), 1000, ct);
        var currentIds = parcels.Select(p => p.CurrentBoundaryId).Where(id => id is not null).Select(id => id!).ToList();
        var areas = currentIds.Count == 0
            ? []
            : (await store.Boundaries.FindAsync(access.OwnerScope, b => currentIds.Contains(b.Id), 1000, ct)).ToDictionary(b => b.ParcelId, b => b.AreaFeddan);

        return new SharedParcels(access.GrantorName, access.Purpose, access.ValidUntil,
            [.. parcels.Select(p => new SharedParcel(p.Id, p.Label, areas.TryGetValue(p.Id, out var a) ? a : null))], withheld);
    }
}
