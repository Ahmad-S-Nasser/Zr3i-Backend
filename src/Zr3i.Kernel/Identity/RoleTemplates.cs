namespace Zr3i.Kernel.Identity;

public sealed record RoleTemplate(string Key, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The built-in role templates, copied into every new organisation where they can then be edited.
/// Placeholder permission sets: module manifests (week 2) will register concrete permissions and refine these.
/// Only org-admin can manage users, roles and grants (action "manage").
/// </summary>
public static class RoleTemplates
{
    public static IReadOnlyList<RoleTemplate> All { get; } =
    [
        new("org-admin", "Organisation admin", ["*:*:*"]),
        new("analyst", "Analyst", ["*:*:read", "*:*:write"]),
        new("viewer", "Viewer", ["*:*:read"]),
        new("field-user", "Field user", ["*:*:read"]),
    ];
}
