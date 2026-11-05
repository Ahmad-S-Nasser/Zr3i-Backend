using System.Diagnostics;

namespace Zr3i.Reports;

/// <summary>Turns report HTML into a PDF. Heavy, so it runs only in the worker (hard rule 6).</summary>
public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(string html, DateTime createdAtUtc, CancellationToken ct);
}

/// <summary>
/// Prints the page with a Chromium-family browser run headless from its command line (Chrome, Edge or a bundled Chromium), which shapes Arabic
/// and mirrors right-to-left pages correctly. The browser is the one at <c>Reports:BrowserPath</c>. With no path set, rendering fails with the
/// code <c>browser_not_configured</c> and the job reports it, never silently skipped. Each render uses its own temporary profile folder, so a
/// browser already open on the machine cannot take over the launch.
/// </summary>
public sealed class ChromiumPdfRenderer(string? browserPath) : IPdfRenderer
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<byte[]> RenderAsync(string html, DateTime createdAtUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath)) throw new InvalidOperationException("browser_not_configured");

        var work = Path.Combine(Path.GetTempPath(), "zr3i-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "in.html");
            var output = Path.Combine(work, "out.pdf");
            await File.WriteAllTextAsync(input, html, new System.Text.UTF8Encoding(false), ct);

            var start = new ProcessStartInfo(browserPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--headless=new", "--disable-gpu", "--no-sandbox", "--no-pdf-header-footer", $"--user-data-dir={Path.Combine(work, "profile")}", $"--print-to-pdf={output}", new Uri(input).AbsoluteUri })
                start.ArgumentList.Add(a);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("pdf_unavailable");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException("pdf_unavailable");

                // Some browsers hand the work to a background process and exit at once, so the file is awaited, not assumed.
                await WaitForCompleteFileAsync(output, limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("pdf_unavailable");
            }

            return PdfDates.Fix(await File.ReadAllBytesAsync(output, ct), createdAtUtc);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Returns once the file exists and its size has stopped changing for a moment.</summary>
    private static async Task WaitForCompleteFileAsync(string path, CancellationToken ct)
    {
        long last = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var length = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (length > 0 && length == last) return;
            last = length;
            await Task.Delay(250, ct);
        }
    }
}

/// <summary>Chromium stamps the current time into a PDF. Replacing it with the freeze time keeps the same snapshot producing the same kind of file.</summary>
public static class PdfDates
{
    public static byte[] Fix(byte[] pdf, DateTime createdAtUtc)
    {
        var stamp = "D:" + createdAtUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var fixedText = System.Text.RegularExpressions.Regex.Replace(text, @"/(CreationDate|ModDate) \((D:[0-9]{14}[^)]*)\)", m =>
        {
            // Same length as before, so the file's byte offsets stay valid.
            var inner = m.Groups[2].Value;
            return stamp.Length > inner.Length ? m.Value : $"/{m.Groups[1].Value} ({stamp.PadRight(inner.Length)})";
        });
        return System.Text.Encoding.Latin1.GetBytes(fixedText);
    }
}
