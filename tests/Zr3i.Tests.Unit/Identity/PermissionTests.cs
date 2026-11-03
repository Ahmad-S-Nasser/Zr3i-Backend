using Zr3i.Kernel.Identity;
using Xunit;

namespace Zr3i.Tests.Unit.Identity;

public class PermissionTests
{
    [Theory]
    [InlineData("verification:parcels:read", true)]
    [InlineData("*:*:read", true)]
    [InlineData("kernel:users:manage", true)]
    [InlineData("verification:parcels", false)]
    [InlineData("verification:parcels:read:extra", false)]
    [InlineData("Verification:parcels:read", false)]
    [InlineData("verification::read", false)]
    [InlineData("", false)]
    public void ValidatesTheModuleResourceActionShape(string value, bool valid)
    {
        Assert.Equal(valid, Permission.IsValid(value));
    }

    [Theory]
    [InlineData("*:*:*", "risk:fri:read", true)]
    [InlineData("risk:*:*", "risk:fri:read", true)]
    [InlineData("risk:*:*", "verification:parcels:read", false)]
    [InlineData("*:*:read", "risk:fri:read", true)]
    [InlineData("*:*:read", "risk:fri:write", false)]
    [InlineData("verification:parcels:read", "verification:parcels:read", true)]
    [InlineData("verification:parcels:read", "verification:farms:read", false)]
    public void GrantedPermissionMatchesRequired(string granted, string required, bool expected)
    {
        Assert.Equal(expected, Permission.Matches(granted, required));
    }

    [Fact]
    public void RoleTemplatesUseOnlyValidPermissionsAndAdminAloneManages()
    {
        Assert.NotEmpty(RoleTemplates.All);
        Assert.All(RoleTemplates.All.SelectMany(t => t.Permissions), p => Assert.True(Permission.IsValid(p), p));

        var canManageUsers = RoleTemplates.All
            .Where(t => t.Permissions.Any(p => Permission.Matches(p, "kernel:users:manage")))
            .Select(t => t.Key);
        Assert.Equal(["org-admin"], canManageUsers);
    }
}
