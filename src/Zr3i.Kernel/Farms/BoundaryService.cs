using Zr3i.Data;
using Zr3i.Geo;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Farms;

/// <param name="Source">One of <see cref="BoundarySources"/>.</param>
/// <param name="CapturedBy">Who or what captured the shape (an agronomist id, a survey firm, an import file).</param>
/// <param name="CapturedAt">When the shape was captured in the field, not when it was uploaded.</param>
/// <param name="AccuracyM">Horizontal accuracy in metres, where the source states one.</param>
public sealed record BoundaryProvenance(string Source, string CapturedBy, DateTime CapturedAt, double? AccuracyM);

/// <summary>
/// Adds boundaries to parcels. A boundary is checked (<see cref="BoundaryValidator"/>), measured in EPSG:32636, and stored
/// as a new immutable row that supersedes the previous one. Nothing is edited and nothing is stored when the polygon is refused.
/// </summary>
public sealed class BoundaryService(KernelStore store, AuditService audit, TimeProvider clock)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <exception cref="NotFoundException">The parcel is not in this tenant.</exception>
    /// <exception cref="ValidationFailedException">Codes from the provenance checks or <see cref="BoundaryValidator"/>, or "capture_older_than_current".</exception>
    public Task<BoundaryVersion> AddAsync(
        TenantScope scope, string actorUserId, string parcelId, PolygonInput polygon, BoundaryProvenance provenance, CancellationToken ct = default) =>
        AddAsync(scope, actorUserId, parcelId, BoundaryValidator.Check(polygon), provenance, ct);

    /// <summary>As above, for a polygon that has already been checked (the bulk import checks each row once).</summary>
    public async Task<BoundaryVersion> AddAsync(
        TenantScope scope, string actorUserId, string parcelId, BoundaryCheck check, BoundaryProvenance provenance, CancellationToken ct = default)
    {
        var parcel = await store.Parcels.GetAsync(scope, parcelId ?? string.Empty, ct) ?? throw new NotFoundException("parcel", parcelId ?? string.Empty);

        var now = clock.GetUtcNow().UtcDateTime;
        var current = Tip(await HistoryAsync(scope, parcel.Id, ct));
        var boundary = CreateVersion(parcel.Id, check, provenance, current, now, actorUserId);
        await store.Boundaries.InsertAsync(scope, boundary, ct);

        parcel.CurrentBoundaryId = boundary.Id;
        await store.Parcels.ReplaceAsync(scope, parcel, ct);
        await audit.RecordAsync(scope, actorUserId, "boundary.added", "boundary", boundary.Id, ct: ct);
        return boundary;
    }

    /// <summary>Every boundary of the parcel, oldest capture first.</summary>
    public Task<IReadOnlyList<BoundaryVersion>> GetHistoryAsync(TenantScope scope, string parcelId, CancellationToken ct = default) =>
        HistoryAsync(scope, parcelId, ct);

    /// <summary>The latest capture that nothing supersedes, worked out from the boundaries themselves, not from the parcel's cached pointer.</summary>
    public async Task<BoundaryVersion?> GetCurrentAsync(TenantScope scope, string parcelId, CancellationToken ct = default) =>
        Tip(await HistoryAsync(scope, parcelId, ct));

    /// <summary>
    /// Builds the next boundary version for a parcel, enforcing the provenance and ordering rules, without writing anything.
    /// <paramref name="current"/> is the boundary being superseded, if any.
    /// </summary>
    /// <exception cref="ValidationFailedException">Provenance codes, the polygon's codes, or "capture_older_than_current".</exception>
    public static BoundaryVersion CreateVersion(
        string parcelId, BoundaryCheck check, BoundaryProvenance provenance, BoundaryVersion? current, DateTime now, string actorUserId)
    {
        var capturedAt = provenance.CapturedAt.Kind == DateTimeKind.Local ? provenance.CapturedAt.ToUniversalTime() : DateTime.SpecifyKind(provenance.CapturedAt, DateTimeKind.Utc);
        var errors = new List<string>();
        if (!BoundarySources.All.Contains(provenance.Source)) errors.Add("invalid_source");
        if (string.IsNullOrWhiteSpace(provenance.CapturedBy)) errors.Add("captured_by_required");
        if (provenance.AccuracyM is { } accuracy && !(double.IsFinite(accuracy) && accuracy >= 0)) errors.Add("invalid_accuracy");
        if (capturedAt > now + ClockSkew) errors.Add("capture_in_future");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        if (!check.IsValid) throw new ValidationFailedException(check.Errors);
        if (current is not null && capturedAt < current.CapturedAt) throw new ValidationFailedException("capture_older_than_current");

        return new BoundaryVersion
        {
            Id = NewId.Create(),   // assigned here so a parcel can point at it before it is written
            ParcelId = parcelId,
            Geometry = GeoJsonPolygon.FromRings(check.Rings.Select(r => r.Select(p => (p.Lon, p.Lat)))),
            AreaM2 = check.AreaM2,
            AreaFeddan = check.AreaFeddan,
            AreaMethodId = GeoConstants.AreaMethodId,
            AreaEpsg = GeoConstants.AreaEpsg,
            CentroidLon = check.Centroid.Lon,
            CentroidLat = check.Centroid.Lat,
            MinLon = check.Bounds.MinLon,
            MinLat = check.Bounds.MinLat,
            MaxLon = check.Bounds.MaxLon,
            MaxLat = check.Bounds.MaxLat,
            Warnings = [.. check.Warnings],
            GeometrySha256 = BoundaryHash.Of(check.Rings),
            Source = provenance.Source,
            CapturedBy = provenance.CapturedBy.Trim(),
            CapturedAt = capturedAt,
            AccuracyM = provenance.AccuracyM,
            Supersedes = current?.Id,
            CreatedAt = now,
            CreatedBy = actorUserId,
        };
    }

    private async Task<IReadOnlyList<BoundaryVersion>> HistoryAsync(TenantScope scope, string parcelId, CancellationToken ct) =>
        [.. (await store.Boundaries.FindAsync(scope, b => b.ParcelId == parcelId, ct: ct))
            .OrderBy(b => b.CapturedAt).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id, StringComparer.Ordinal)];

    private static BoundaryVersion? Tip(IReadOnlyList<BoundaryVersion> history)
    {
        var superseded = history.Where(b => b.Supersedes is not null).Select(b => b.Supersedes).ToHashSet();
        return history.Where(b => !superseded.Contains(b.Id)).LastOrDefault();
    }
}
