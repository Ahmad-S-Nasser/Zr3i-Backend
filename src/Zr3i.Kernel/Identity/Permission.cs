using System.Text.RegularExpressions;

namespace Zr3i.Kernel.Identity;

/// <summary>A permission is "module:resource:action". A granted permission may use "*" for any segment.</summary>
public static partial class Permission
{
    [GeneratedRegex("^(\\*|[a-z][a-z0-9-]*):(\\*|[a-z][a-z0-9-]*):(\\*|[a-z][a-z0-9-]*)$")]
    private static partial Regex Shape();

    public static bool IsValid(string? value) => value is not null && Shape().IsMatch(value);

    /// <summary>True if <paramref name="granted"/> (may hold wildcards) covers <paramref name="required"/> (concrete).</summary>
    public static bool Matches(string granted, string required)
    {
        if (!IsValid(granted) || !IsValid(required) || required.Contains('*', StringComparison.Ordinal)) return false;

        var g = granted.Split(':');
        var r = required.Split(':');
        for (var i = 0; i < 3; i++)
            if (g[i] != "*" && g[i] != r[i]) return false;
        return true;
    }
}
