using System.Reflection;
using Zr3i.Data;
using Zr3i.Kernel;
using Xunit;

namespace Zr3i.Tests.Integration;

/// <summary>
/// CLAUDE.md rule 1. Finds every tenant-owned entity in the Kernel, seeds two tenants in each, and asserts that
/// no read, update or delete crosses. Adding an entity without covering it here is not possible: discovery is by reflection.
/// </summary>
[Collection(MongoCollection.Name)]
public class TwoTenantIsolationTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("iso-tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("iso-tenant-b");

    private static IEnumerable<Type> KernelEntityTypes() =>
        typeof(KernelStore).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantEntity).IsAssignableFrom(t));

    public static IEnumerable<object[]> EntityTypes() => KernelEntityTypes().Select(t => new object[] { t });

    [Fact]
    public void EveryKernelEntityIsReachableOnlyThroughAScopedRepository()
    {
        var exposed = typeof(KernelStore).GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType)
            .Where(t => t.GetGenericTypeDefinition() == typeof(TenantRepository<>) || t.GetGenericTypeDefinition() == typeof(AppendOnlyRepository<>))
            .Select(t => t.GetGenericArguments()[0])
            .ToHashSet();

        Assert.NotEmpty(exposed);
        Assert.Equal(KernelEntityTypes().OrderBy(t => t.Name), exposed.OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public async Task NoOperationCrossesBetweenTwoTenants(Type entityType)
    {
        await (Task)typeof(TwoTenantIsolationTests)
            .GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(entityType)
            .Invoke(this, null)!;
    }

    private async Task Check<T>() where T : class, ITenantEntity, new()
    {
        var name = "iso_" + typeof(T).Name.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N");
        var collection = mongo.Database.GetCollection<T>(name);
        var tenantRepo = new TenantRepository<T>(collection);
        var appendOnly = new AppendOnlyRepository<T>(collection);

        var a = new T();
        var b = new T();
        await tenantRepo.InsertAsync(A, a);
        await tenantRepo.InsertAsync(B, b);

        // reads
        Assert.Null(await tenantRepo.GetAsync(A, b.Id));
        Assert.Null(await tenantRepo.GetAsync(B, a.Id));
        Assert.Equal([a.Id], (await tenantRepo.FindAsync(A, _ => true)).Select(x => x.Id));
        Assert.Equal([b.Id], (await tenantRepo.FindAsync(B, _ => true)).Select(x => x.Id));
        Assert.Equal([a.Id], (await appendOnly.FindAsync(A, _ => true)).Select(x => x.Id));
        Assert.Equal(1, await tenantRepo.CountAsync(A, _ => true));
        Assert.Equal([a.Id], (await tenantRepo.FindPageAsync(A, _ => true, null, 10)).Items.Select(x => x.Id));

        // writes
        Assert.False(await tenantRepo.ReplaceAsync(A, new T { Id = b.Id }));
        Assert.False(await tenantRepo.DeleteAsync(A, b.Id));
        Assert.NotNull(await tenantRepo.GetAsync(B, b.Id));

        // an unset scope can never be used
        Assert.Throws<InvalidOperationException>(() => default(TenantScope).TenantId.ToString());
    }
}
