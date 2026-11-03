using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>Hard rule 1: no code touches the Mongo driver outside the repository layer (Zr3i.Data).</summary>
public class MongoDriverIsolationTests
{
    private static readonly string[] OtherSrcAssemblies =
    [
        "Zr3i.Kernel", "Zr3i.Geo", "Zr3i.Modules.Verification", "Zr3i.Modules.Risk",
        "Zr3i.Reports", "Zr3i.Imagery", "Zr3i.Web", "Zr3i.Worker",
    ];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void AssemblyDoesNotUseTheMongoDriver(string name)
    {
        var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
        Assert.DoesNotContain(refs, r => r.StartsWith("MongoDB", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheDataProjectReferencesAMongoPackage()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f) != "Zr3i.Data")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Include=""MongoDB", RegexOptions.IgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    public static IEnumerable<object[]> Assemblies() => OtherSrcAssemblies.Select(a => new object[] { a });

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found above the test output.");
    }
}
