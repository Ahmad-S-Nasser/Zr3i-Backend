using MongoDB.Driver;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class KernelIndexesTests(MongoFixture mongo)
{
    [Fact]
    public async Task TwoUsersCannotShareAnEmailAndMigrationRunsOnce()
    {
        var db = mongo.Database.Client.GetDatabase("zr3i_test_idx_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(db, [new KernelIndexesMigration()]);
        Assert.Equal(["0001-kernel-indexes"], await runner.RunAsync());
        Assert.Empty(await runner.RunAsync());

        var users = new KernelStore(new DataStore(db)).Users;
        await users.InsertAsync(PlatformTenant.Scope, new User { Email = "a@example.test" });
        await Assert.ThrowsAnyAsync<MongoWriteException>(() =>
            users.InsertAsync(PlatformTenant.Scope, new User { Email = "a@example.test" }));

        // The same email in a different tenant is a different row and is allowed.
        await users.InsertAsync(TenantScope.ForTenant("other"), new User { Email = "a@example.test" });
        await mongo.Database.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
    }
}
