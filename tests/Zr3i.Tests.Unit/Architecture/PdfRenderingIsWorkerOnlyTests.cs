using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

/// <summary>Printing a PDF starts a browser, which is heavy (hard rule 6). Only the reports project starts one, and only the worker registers it; the web app queues the job.</summary>
public class PdfRenderingIsWorkerOnlyTests
{
    [Theory]
    [InlineData("Zr3i.Kernel")]
    [InlineData("Zr3i.Web")]
    [InlineData("Zr3i.Data")]
    [InlineData("Zr3i.Modules.Risk")]
    [InlineData("Zr3i.Modules.Verification")]
    public void ThisProjectNeverStartsABrowser(string project)
    {
        var offenders = Directory.GetFiles(Path.Combine(FindRepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("print-to-pdf", StringComparison.Ordinal) || t.Contains("ChromiumPdfRenderer", StringComparison.Ordinal) || t.Contains("IPdfRenderer", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyTheWorkerRegistersTheRenderer()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var registering = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(f).Contains("new Zr3i.Reports.ChromiumPdfRenderer(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["WorkerHost.cs"], registering);
    }

    [Fact]
    public void NoProjectNeedsABrowserAutomationPackage()
    {
        var users = Directory.GetFiles(Path.Combine(FindRepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Puppeteer", StringComparison.OrdinalIgnoreCase) || File.ReadAllText(f).Contains("Playwright", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(users);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Zr3i.slnx not found.");
    }
}
