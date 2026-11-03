using Zr3i.Data;
using Zr3i.Kernel.Audit;
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
    public AppendOnlyRepository<AuditEvent> Audit { get; } = data.AppendOnly<AuditEvent>(Collections.AuditEvents);
}
