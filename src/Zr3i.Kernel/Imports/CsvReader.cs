using System.Text;

namespace Zr3i.Kernel.Imports;

/// <param name="Line">The physical line the record starts on (1-based), so errors point at the right row of the spreadsheet.</param>
public sealed record CsvRecord(int Line, string[] Fields);

/// <param name="Error">Null on success; otherwise a stable code ("unterminated_quote").</param>
public sealed record CsvParseResult(IReadOnlyList<CsvRecord> Records, char Delimiter, string? Error, int ErrorLine);

/// <summary>
/// A small RFC 4180 reader. It accepts any line ending, quoted fields with embedded delimiters, quotes and newlines,
/// and a byte-order mark, and detects comma, semicolon or tab from the header (Excel in many locales writes semicolons).
/// Rows are returned as they are, ragged or not; the importer reports a wrong column count per row.
/// </summary>
public static class CsvReader
{
    private static readonly char[] Candidates = [',', ';', '\t'];

    public static CsvParseResult Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var delimiter = DetectDelimiter(text);

        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var fieldStarted = false;
        var quoteLine = 1;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            var blank = fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]);
            if (!blank) records.Add(new CsvRecord(recordLine, [.. fields]));
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                quoteLine = line;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRecord();
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (inQuotes) return new CsvParseResult(records, delimiter, "unterminated_quote", quoteLine);
        if (field.Length > 0 || fields.Count > 0 || fieldStarted) EndRecord();
        return new CsvParseResult(records, delimiter, null, 0);
    }

    /// <summary>The delimiter that appears most often outside quotes on the first non-empty line; a comma when there is none.</summary>
    private static char DetectDelimiter(string text)
    {
        var counts = Candidates.ToDictionary(c => c, _ => 0);
        var inQuotes = false;
        var seenContent = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; seenContent = true; continue; }
            if (!inQuotes && c is '\r' or '\n')
            {
                if (seenContent) break;
                continue;
            }

            seenContent = true;
            if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }

        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value == 0 ? ',' : best.Key;
    }
}
