using System.Reflection;
using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>
/// Fetching satellite data is slow, third-party and heavy, so it happens only in the worker (hard rule 6). The web app queues searches
/// through the Kernel and never loads the imagery project or its native raster libraries.
/// </summary>
public class ImageryIsWorkerOnlyTests
{
    [Fact]
    public void TheWebAppReferencesNeitherTheImageryProjectNorARasterLibrary()
    {
        var refs = Assembly.Load("Zr3i.Web").GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        Assert.DoesNotContain(refs, r => r.StartsWith("Zr3i.Imagery", StringComparison.Ordinal) || r.StartsWith("MaxRev", StringComparison.Ordinal) || r.StartsWith("Gdal", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProjectOtherThanTheWorkerReferencesTheImageryProject()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f) is not ("Zr3i.Imagery" or "Zr3i.Worker"))
            .Where(f => File.ReadAllText(f).Contains("Zr3i.Imagery", StringComparison.Ordinal))
            .Select(Path.GetFileName)
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
