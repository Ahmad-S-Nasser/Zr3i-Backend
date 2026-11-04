using Zr3i.Data;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Methods;
using Zr3i.Kernel.Identity;

namespace Zr3i.Kernel;

/// <summary>
/// Every Kernel collection, reachable only as a scope-requiring repository.
/// A test asserts that each tenant-owned entity in this assembly is listed here.
/// </summary>
public sealed class KernelStore(DataStore data)
{
    public TenantRepository<Organisation> Organisations { get; } = data.Tenant<Organisation>(Collections.Organisations);
    public TenantRepository<User> Users { get; } = data.Tenant<User>(Collections.Users);
    public TenantRepository<Membership> Memberships { get; } = data.Tenant<Membership>(Collections.Memberships);
    public TenantRepository<Role> Roles { get; } = data.Tenant<Role>(Collections.Roles);
    public TenantRepository<Invitation> Invitations { get; } = data.Tenant<Invitation>(Collections.Invitations);
    public TenantRepository<Session> Sessions { get; } = data.Tenant<Session>(Collections.Sessions);
    public TenantRepository<LoginChallenge> LoginChallenges { get; } = data.Tenant<LoginChallenge>(Collections.LoginChallenges);
    public TenantRepository<Portfolio> Portfolios { get; } = data.Tenant<Portfolio>(Collections.Portfolios);
    public TenantRepository<Farm> Farms { get; } = data.Tenant<Farm>(Collections.Farms);
    public TenantRepository<Parcel> Parcels { get; } = data.Tenant<Parcel>(Collections.Parcels);
    public TenantRepository<ParcelSeason> ParcelSeasons { get; } = data.Tenant<ParcelSeason>(Collections.ParcelSeasons);
    public TenantRepository<CustomFieldDefinition> CustomFieldDefinitions { get; } = data.Tenant<CustomFieldDefinition>(Collections.CustomFieldDefinitions);

    public TenantRepository<Farmer> Farmers { get; } = data.Tenant<Farmer>(Collections.Farmers);
    public TenantRepository<ConsentRequest> ConsentRequests { get; } = data.Tenant<ConsentRequest>(Collections.ConsentRequests);

    /// <summary>The consent ledger (hard rule 5): grants and withdrawals are only ever added, never changed.</summary>
    public AppendOnlyRepository<ConsentEvent> ConsentEvents { get; } = data.AppendOnly<ConsentEvent>(Collections.ConsentEvents);

    /// <summary>What each scene says about each parcel: immutable results, only ever added.</summary>
    public AppendOnlyRepository<ParcelObservation> ParcelObservations { get; } = data.AppendOnly<ParcelObservation>(Collections.ParcelObservations);

    public TenantRepository<Scene> Scenes { get; } = data.Tenant<Scene>(Collections.Scenes);
    public AppendOnlyRepository<Zr3i.Kernel.Crops.CropConfigVersion> CropConfigVersions { get; } = data.AppendOnly<Zr3i.Kernel.Crops.CropConfigVersion>(Collections.CropConfigVersions);
    public AppendOnlyRepository<Zr3i.Kernel.Crops.CropConfigActivation> CropConfigActivations { get; } = data.AppendOnly<Zr3i.Kernel.Crops.CropConfigActivation>(Collections.CropConfigActivations);
    public TenantRepository<MethodVersion> MethodVersions { get; } = data.Tenant<MethodVersion>(Collections.MethodVersions);
    public TenantRepository<AccessRequest> AccessRequests { get; } = data.Tenant<AccessRequest>(Collections.AccessRequests);
    public TenantRepository<AccountType> AccountTypes { get; } = data.Tenant<AccountType>(Collections.AccountTypes);
    public TenantRepository<ImportBatch> ImportBatches { get; } = data.Tenant<ImportBatch>(Collections.ImportBatches);
    public TenantRepository<ImportRow> ImportRows { get; } = data.Tenant<ImportRow>(Collections.ImportRows);

    /// <summary>The uploaded file exactly as received, in chunks. Append-only: what was uploaded stays provable.</summary>
    public AppendOnlyRepository<ImportChunk> ImportChunks { get; } = data.AppendOnly<ImportChunk>(Collections.ImportChunks);

    /// <summary>Boundaries are append-only: a re-capture is a new row, never an edit.</summary>
    public AppendOnlyRepository<BoundaryVersion> Boundaries { get; } = data.AppendOnly<BoundaryVersion>(Collections.Boundaries);

    public AppendOnlyRepository<AuditEvent> Audit { get; } = data.AppendOnly<AuditEvent>(Collections.AuditEvents);
}
