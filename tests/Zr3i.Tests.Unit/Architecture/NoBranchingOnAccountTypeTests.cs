using System.Text.RegularExpressions;
using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>
/// CLAUDE.md rule 2: core code never branches on account type. The five types are data presets, so their names may be written in
/// exactly one file (the built-in presets). Elsewhere a type name must never sit in a comparison, a switch or a pattern. (The
/// word "farmer" is also an entity and "bank" is also a word, so plain mentions such as an audit object type are not the issue.)
/// </summary>
public class NoBranchingOnAccountTypeTests
{
    private const string Names = "zr3i-admin|bank|insurance|company|farmer";

    private static readonly Regex Branching = new(
        $"(==|!=)\\s*\"({Names})\"" +                 // x == "bank"
        $"|\"({Names})\"\\s*(==|!=)" +                // "bank" == x
        $"|\\bcase\\s+\"({Names})\"" +                // case "bank":
        $"|\"({Names})\"\\s*(=>|:\\s*$)" +            // "bank" => ...
        $"|\\bis\\s+(not\\s+)?\"({Names})\"" +        // x is "bank"
        $"|\\.Equals\\(\\s*\"({Names})\"" +           // x.Equals("bank")
        $"|\\.Contains\\(\\s*\"({Names})\"" +         // keys.Contains("bank")
        $"|\\bStartsWith\\(\\s*\"({Names})\"",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("if (org.AccountType == \"bank\") Hide();")]
    [InlineData("if (\"farmer\" != type) return;")]
    [InlineData("switch (t) { case \"company\": break; }")]
    [InlineData("var home = t switch { \"insurance\" => 1, _ => 0 };")]
    [InlineData("if (t is \"zr3i-admin\") Allow();")]
    [InlineData("if (t is not \"bank\") Deny();")]
    [InlineData("if (type.Equals(\"farmer\")) X();")]
    [InlineData("if (new[] { \"a\" }.Contains(\"bank\")) X();")]
    public void ThePatternCatchesRealBranching(string code)
    {
        Assert.Matches(Branching, code);
    }

    [Theory]
    [InlineData("throw new NotFoundException(\"farmer\", id);")]
    [InlineData("await audit.RecordAsync(scope, actor, \"farmer.created\", \"farmer\", id);")]
    [InlineData("var role = a ? \"owner\" : \"farmer\";")]
    public void ThePatternLeavesPlainMentionsAlone(string code)
    {
        Assert.DoesNotMatch(Branching, code);
    }

    [Fact]
    public void NoSourceFileBranchesOnAnAccountTypeAndTheNamesAreOnlyWrittenInThePresets()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && Path.GetFileName(f) != "BuiltInAccountTypes.cs")
            .ToList();

        var offenders = files.Where(f => Branching.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(src, f)).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }
}
