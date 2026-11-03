using Zr3i.Data;
using Xunit;

namespace Zr3i.Tests.Unit.Data;

public class TenantScopeTests
{
    [Fact]
    public void ForTenantKeepsTheId()
    {
        Assert.Equal("t-1", TenantScope.ForTenant("t-1").TenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTenantRejectsMissingId(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => TenantScope.ForTenant(id!));
    }

    [Fact]
    public void ScopesWithTheSameIdAreEqual()
    {
        Assert.Equal(TenantScope.ForTenant("a"), TenantScope.ForTenant("a"));
        Assert.NotEqual(TenantScope.ForTenant("a"), TenantScope.ForTenant("b"));
    }

    [Fact]
    public void DefaultScopeIsNotUsable()
    {
        Assert.Throws<InvalidOperationException>(() => _ = default(TenantScope).TenantId);
    }
}
