using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zr3i.Reports;

/// <summary>One labelled value near the top of a report. The key is a code; the renderer supplies the words in English or Arabic.</summary>
public sealed record SnapshotFact(string Key, string? Value);

/// <summary>
/// Everything a report says, with no words in any language: codes, numbers, ids and dates. English and Arabic pages, the CSV and the PDF
/// are all made from this one object, so they cannot disagree. Its hash is the report's identity.
/// </summary>
public sealed record ReportSnapshot(
    string Kind,
    string? Season,
    string Organisation,
    string Subject,
    DateTime GeneratedAtUtc,
    string? MethodId,
    string? MethodVersion,
    string? Maturity,
    string Accuracy,
    bool Synthetic,
    IReadOnlyList<SnapshotFact> Facts,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int RowsTotal)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public string Sha256() => Hash(Encoding.UTF8.GetBytes(ToJson()));

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static ReportSnapshot FromJson(string json) => JsonSerializer.Deserialize<ReportSnapshot>(json, Options) ?? throw new InvalidOperationException("The snapshot is empty.");
}
