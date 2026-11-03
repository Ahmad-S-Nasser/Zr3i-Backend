using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>
/// CLAUDE.md rule 2: core code never branches on account type. The five types are data presets, so their names may appear
/// as literals in exactly one file (the built-in presets) and nowhere else in the source.
/// </summary>
public class NoBranchingOnAccountTypeTests
{
    private static readonly string[] TypeNames = ["\"zr3i-admin\"", "\"bank\"", "\"insurance\"", "\"company\"", "\"farmer\""];

    [Fact]
    public void AccountTypeNamesAppearOnlyInTheBuiltInPresets()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;

        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Where(f => Path.GetFileName(f) != "BuiltInAccountTypes.cs")
            .Where(f => { var text = File.ReadAllText(f); return TypeNames.Any(n => text.Contains(n, StringComparison.Ordinal)); })
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }
}
