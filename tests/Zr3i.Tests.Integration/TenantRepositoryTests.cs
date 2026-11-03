using Zr3i.Data;
using Xunit;

namespace Zr3i.Tests.Integration;

public sealed class Widget : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

[Collection(MongoCollection.Name)]
public class TenantRepositoryTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("tenant-b");

    private TenantRepository<Widget> NewRepo() =>
        new(mongo.Database.GetCollection<Widget>("widgets_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task InsertStampsTheTenantAndGeneratesAnId()
    {
        var repo = NewRepo();
        var w = new Widget { Name = "x" };

        await repo.InsertAsync(A, w);

        Assert.Equal("tenant-a", w.TenantId);
        Assert.False(string.IsNullOrEmpty(w.Id));
        Assert.Equal("x", (await repo.GetAsync(A, w.Id))!.Name);
    }

    [Fact]
    public async Task InsertRejectsAnEntityOwnedByAnotherTenant()
    {
        var repo = NewRepo();
        var w = new Widget { Name = "x", TenantId = "tenant-b" };

        await Assert.ThrowsAsync<TenantMismatchException>(() => repo.InsertAsync(A, w));
    }

    [Fact]
    public async Task OneTenantCannotReadAnotherTenantsData()
    {
        var repo = NewRepo();
        var a = new Widget { Name = "a-widget" };
        var b = new Widget { Name = "b-widget" };
        await repo.InsertAsync(A, a);
        await repo.InsertAsync(B, b);

        Assert.Null(await repo.GetAsync(A, b.Id));
        Assert.Null(await repo.GetAsync(B, a.Id));
        Assert.Equal(["a-widget"], (await repo.FindAsync(A, _ => true)).Select(w => w.Name));
        Assert.Equal(["b-widget"], (await repo.FindAsync(B, _ => true)).Select(w => w.Name));
        Assert.Equal(1, await repo.CountAsync(A, _ => true));
    }

    [Fact]
    public async Task OneTenantCannotChangeOrDeleteAnotherTenantsData()
    {
        var repo = NewRepo();
        var b = new Widget { Name = "b-widget" };
        await repo.InsertAsync(B, b);

        var forged = new Widget { Id = b.Id, Name = "hijacked" };
        Assert.False(await repo.ReplaceAsync(A, forged));
        Assert.False(await repo.DeleteAsync(A, b.Id));

        Assert.Equal("b-widget", (await repo.GetAsync(B, b.Id))!.Name);
    }

    [Fact]
    public async Task ReplaceKeepsTheOwnerTenant()
    {
        var repo = NewRepo();
        var w = new Widget { Name = "one" };
        await repo.InsertAsync(A, w);

        var edited = new Widget { Id = w.Id, Name = "two", TenantId = "tenant-b" };
        Assert.True(await repo.ReplaceAsync(A, edited));

        var back = await repo.GetAsync(A, w.Id);
        Assert.Equal("two", back!.Name);
        Assert.Equal("tenant-a", back.TenantId);
        Assert.Null(await repo.GetAsync(B, w.Id));
    }

    [Fact]
    public async Task PagesWalkAllRowsOfOneTenantWithACursor()
    {
        var repo = NewRepo();
        for (var i = 0; i < 5; i++) await repo.InsertAsync(A, new Widget { Name = $"a{i}" });
        for (var i = 0; i < 3; i++) await repo.InsertAsync(B, new Widget { Name = $"b{i}" });

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await repo.FindPageAsync(A, _ => true, cursor, 2);
            seen.AddRange(page.Items.Select(w => w.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.All(seen, n => Assert.StartsWith("a", n, StringComparison.Ordinal));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }
}
