using System.Reflection;
using Zr3i.Data;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class AppendOnlyRepositoryTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("tenant-b");

    private AppendOnlyRepository<Widget> NewRepo() =>
        new(mongo.Database.GetCollection<Widget>("events_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void ExposesOnlyInsertAndRead()
    {
        var verbs = typeof(AppendOnlyRepository<Widget>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Equal(new HashSet<string> { "InsertAsync", "InsertManyAsync", "GetAsync", "FindAsync", "FindOrderedAsync", "FindPageAsync" }, verbs);
    }

    [Fact]
    public async Task InsertedRowsAreVisibleOnlyToTheirTenant()
    {
        var repo = NewRepo();
        var w = new Widget { Name = "event" };
        await repo.InsertAsync(A, w);

        Assert.NotNull(await repo.GetAsync(A, w.Id));
        Assert.Null(await repo.GetAsync(B, w.Id));
        Assert.Empty(await repo.FindAsync(B, _ => true));
    }

    [Fact]
    public async Task InsertingTheSameIdTwiceFails()
    {
        var repo = NewRepo();
        var w = new Widget { Name = "event" };
        await repo.InsertAsync(A, w);

        var again = new Widget { Id = w.Id, Name = "overwrite" };
        await Assert.ThrowsAnyAsync<Exception>(() => repo.InsertAsync(A, again));
        Assert.Equal("event", (await repo.GetAsync(A, w.Id))!.Name);
    }
}
