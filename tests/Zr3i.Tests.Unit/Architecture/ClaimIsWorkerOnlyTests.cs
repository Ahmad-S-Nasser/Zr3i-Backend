using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>
/// The job claim is the one deliberate cross-tenant read. Only the data layer, the Kernel's job runner and the worker host
/// may touch it; the web app and every module go through the tenant-scoped JobQueue.
/// </summary>
public class ClaimIsWorkerOnlyTests
{
    private static readonly string[] Forbidden = ["JobClaimer", "CreateJobClaimer", "ClaimNextAsync"];

    [Fact]
    public void OnlyTheDataLayerTheJobRunnerAndTheWorkerReferenceTheClaimer()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var allowed = new[] { Path.Combine(src, "Zr3i.Data"), Path.Combine(src, "Zr3i.Worker"), Path.Combine(src, "Zr3i.Kernel", "Jobs") };

        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !allowed.Any(a => f.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            .Where(f => { var text = File.ReadAllText(f); return Forbidden.Any(w => text.Contains(w, StringComparison.Ordinal)); })
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
