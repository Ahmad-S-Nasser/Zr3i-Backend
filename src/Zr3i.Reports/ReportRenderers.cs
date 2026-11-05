using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zr3i.Reports;

/// <summary>The words for one cell: codes become English or Arabic text, numbers and names stay as they are.</summary>
internal static class Cells
{
    public static string Text(string language, string column, string value)
    {
        if (string.IsNullOrEmpty(value)) return ReportStrings.Text(language, "val.empty");
        var key = column switch
        {
            "verdict" or "status" => $"val.{value}",
            "band" => value.Length == 1 ? $"val.band.{value}" : "val.no_band",
            "crop" => $"val.crop.{value}",
            _ => null,
        };
        return key is not null && ReportStrings.For(language).ContainsKey(key) ? ReportStrings.Text(language, key) : value;
    }
}

/// <summary>
/// The A4 page for a snapshot, in one language. Colours are the design-system tokens; layout uses logical properties only, so the Arabic page
/// mirrors the English one. The hash is in a fixed footer so it prints on every page.
/// </summary>
public static class ReportHtml
{
    public static string Render(ReportSnapshot s, string language, string snapshotSha256)
    {
        var ar = language == "ar";
        string T(string key, params (string, string)[] vars) => ReportStrings.Text(language, key, vars);
        static string E(string text) => Escape(text);
        static string Num(string text) => $"<bdi dir=\"ltr\">{Escape(text)}</bdi>";

        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"").Append(language).Append("\" dir=\"").Append(ar ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<title>").Append(E(T($"title.{s.Kind}"))).Append(" · ").Append(E(s.Subject)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n");

        if (s.Synthetic) sb.Append("<div class=\"synthetic\" role=\"note\">").Append(E(T("synthetic"))).Append("</div>\n<div class=\"mark\" aria-hidden=\"true\">").Append(E(T("synthetic"))).Append("</div>\n");

        sb.Append("<header class=\"head\"><span class=\"brand\">").Append(E(T("brand"))).Append("</span>");
        sb.Append("<h1>").Append(E(T($"title.{s.Kind}"))).Append("</h1></header>\n");

        if (s.MethodId is not null)
            sb.Append("<p class=\"maturity\"><span class=\"badge\">").Append(E(T("maturity.specified"))).Append("</span> ")
              .Append(E(T("meta.method"))).Append(": ").Append(Num($"{s.MethodId} · v{s.MethodVersion}")).Append("</p>\n");

        sb.Append("<dl class=\"meta\">");
        void Meta(string label, string valueHtml) => sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(valueHtml).Append("</dd></div>");
        Meta(T("meta.organisation"), E(s.Organisation));
        Meta(T("meta.subject"), E(s.Subject));
        if (s.Season is not null) Meta(T("meta.season"), Num(s.Season));
        Meta(T("meta.generated"), Num(s.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        Meta(T("meta.accuracy"), E(T($"accuracy.{s.Accuracy}")));
        sb.Append("</dl>\n");

        sb.Append("<dl class=\"facts\">");
        foreach (var f in s.Facts)
            sb.Append("<div><dt>").Append(E(T($"fact.{f.Key}"))).Append("</dt><dd>").Append(f.Value is null ? E(T("val.empty")) : Num(f.Value)).Append("</dd></div>");
        sb.Append("</dl>\n");

        sb.Append("<table>\n<thead><tr>");
        foreach (var c in s.Columns) sb.Append("<th>").Append(E(T($"col.{c}"))).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in s.Rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < s.Columns.Count; i++)
            {
                var col = s.Columns[i];
                var raw = i < row.Count ? row[i] : string.Empty;
                var text = Cells.Text(language, col, raw);
                var numeric = col is "measured" or "declared" or "gap" or "area" or "cri" or "parcels" or "fri" or "awcri" or "parcel";
                sb.Append(numeric ? "<td class=\"n\">" + Num(text) : "<td>" + E(text)).Append("</td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody></table>\n");
        if (s.RowsTotal > s.Rows.Count) sb.Append("<p class=\"note\">").Append(E(T("note.shown", ("shown", s.Rows.Count.ToString(CultureInfo.InvariantCulture)), ("total", s.RowsTotal.ToString(CultureInfo.InvariantCulture))))).Append("</p>\n");
        sb.Append("<p class=\"note\">").Append(E(T("note.facts"))).Append("</p>\n");
        if (s.MethodId is not null) sb.Append("<p class=\"note\">").Append(E(T("note.risk"))).Append("</p>\n");
        sb.Append("<p class=\"note\">").Append(E(T("note.unit"))).Append(' ').Append(E(T("note.frozen"))).Append("</p>\n");
        sb.Append("<footer class=\"foot\"><span>").Append(E(T("meta.hash"))).Append(": </span><code dir=\"ltr\">").Append(E(snapshotSha256)).Append("</code></footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Escapes only what HTML needs. (WebUtility.HtmlEncode also turns the middle dot into a numeric reference, which makes the text hard to search.)</summary>
    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    // Token values from docs/design-system/tokens.json: zr-field, mist, ink, ink-muted, line, sunken, synthetic-bg, accent-ink, maturity-bg, maturity-ink.
    private const string Css = """
        @page { size: A4; margin: 16mm 14mm 20mm; }
        :root { --field:#17472d; --mist:#dae9e4; --ink:#131e29; --muted:#4a5b66; --line:#c9d9d3; --sunken:#e6f0ec; --syn-bg:#fff1cf; --syn-ink:#6e4a00; --mat-bg:#ece7f3; --mat-ink:#4b3a73; }
        * { box-sizing: border-box; }
        body { margin: 0; color: var(--ink); font: 10.5pt/1.5 Lato, "Noto Sans Arabic", system-ui, sans-serif; }
        .head { display: flex; align-items: baseline; gap: 12px; border-block-end: 3px solid var(--field); padding-block-end: 6px; }
        .brand { color: var(--field); font-weight: 800; font-size: 14pt; }
        h1 { margin: 0; font: 700 18pt/1.2 Roboto, Cairo, system-ui, sans-serif; }
        .maturity { margin-block: 10px; }
        .badge { background: var(--mat-bg); color: var(--mat-ink); border: 1px solid var(--mat-ink); border-radius: 6px; padding: 2px 8px; font-weight: 700; }
        .meta, .facts { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px 16px; margin-block: 12px; }
        .meta div, .facts div { border-inline-start: 3px solid var(--line); padding-inline-start: 8px; }
        .facts div { background: var(--sunken); border-radius: 6px; padding: 6px 10px; border-inline-start: 0; }
        dt { color: var(--muted); font-size: 9pt; }
        dd { margin: 0; font-weight: 700; margin-inline-start: 0; }
        table { width: 100%; border-collapse: collapse; margin-block: 12px; }
        th { background: var(--field); color: #fff; text-align: start; padding: 6px 8px; font-size: 9.5pt; }
        td { border-block-end: 1px solid var(--line); padding: 5px 8px; vertical-align: top; }
        td.n { text-align: end; }
        tr { break-inside: avoid; }
        .note { color: var(--muted); font-size: 9pt; margin-block: 4px; }
        .synthetic { background: var(--syn-bg); color: var(--syn-ink); font-weight: 700; padding: 6px 10px; border-radius: 6px; margin-block-end: 10px; }
        .mark { position: fixed; inset-inline-start: 0; inset-block-start: 45%; width: 100%; text-align: center; font: 700 26pt/1 Roboto, Cairo, sans-serif; color: var(--syn-ink); opacity: .10; transform: rotate(-20deg); pointer-events: none; }
        .foot { position: fixed; inset-inline: 0; inset-block-end: 0; border-block-start: 1px solid var(--line); background: #fff; padding-block: 4px; font-size: 8pt; color: var(--muted); }
        code { font-family: "Roboto Mono", ui-monospace, Menlo, monospace; }
        """;
}

/// <summary>The snapshot's rows as a CSV, with code headers (so the file is the same in both languages) and cells that a spreadsheet will not run as formulas.</summary>
public static class ReportCsv
{
    public static string Render(ReportSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', s.Columns.Select(Cell))).Append("\r\n");
        foreach (var row in s.Rows) sb.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A cell that starts with = + - @ tab or return is read as a formula by Excel and Sheets, so it gets a leading apostrophe. Plain numbers are left alone.</summary>
    public static string Cell(string? value)
    {
        value ??= string.Empty;
        var risky = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (risky) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

/// <summary>The list of files in a report with their SHA-256 hashes, so any file can be checked against what was frozen.</summary>
public static class ReportManifest
{
    public sealed record Entry(string Name, long Bytes, string Sha256);

    public static string Render(string reportId, string kind, string snapshotSha256, DateTime createdAtUtc, IEnumerable<Entry> files)
    {
        var ordered = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(new { reportId, kind, snapshotSha256, createdAt = createdAtUtc.ToString("O", CultureInfo.InvariantCulture), files = ordered },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
