using MongoDB.Bson;
using MongoDB.Driver;
using Zr3i.Kernel;
using Zr3i.Data;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AuditLogTests(MongoFixture mongo)
{
    [Fact]
    public async Task SignInInvitationAndOrganisationEventsAreRecordedInTheRightTenant()
    {
        var k = new KernelHarness(mongo);
        var (org, user, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");
        await k.SignIn.BeginAsync("admin@delta.example", "wrong password here");
        await k.FullSignInAsync("admin@delta.example", secret);

        var orgEvents = await k.Store.Audit.FindAsync(TenantScope.ForTenant(org.Id), _ => true);
        var orgActions = orgEvents.Select(e => e.Action).ToList();
        Assert.Contains("organisation.created", orgActions);
        Assert.Contains("invitation.created", orgActions);
        Assert.Contains("invitation.accepted", orgActions);

        var platformEvents = await k.Store.Audit.FindAsync(PlatformTenant.Scope, _ => true);
        var failed = platformEvents.Single(e => e.Action == "signin.password" && e.Outcome == "denied");
        Assert.Equal(user.Id, failed.ActorUserId);
        Assert.Contains(platformEvents, e => e.Action == "signin.completed" && e.Outcome == "ok");
        Assert.All(platformEvents.Concat(orgEvents), e => Assert.NotEqual(default, e.At));
    }

    [Fact]
    public async Task AuditEventsNeverContainPasswordsOrSecrets()
    {
        var k = new KernelHarness(mongo);
        var (_, _, secret) = await k.OrgWithAdminAsync("Delta Agro", "admin@delta.example");
        await k.FullSignInAsync("admin@delta.example", secret);

        var all = await k.Database.GetCollection<BsonDocument>(Collections.AuditEvents)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        var text = string.Join('\n', all.Select(d => d.ToJson()));

        Assert.DoesNotContain(KernelHarness.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditRepositoryOfferedByTheKernelIsAppendOnly()
    {
        var k = new KernelHarness(mongo);
        Assert.IsType<AppendOnlyRepository<Zr3i.Kernel.Audit.AuditEvent>>(k.Store.Audit);
    }
}
