using Zr3i.Data;

namespace Zr3i.Kernel.Imports;

public static class ImportStatus
{
    public const string Uploaded = "uploaded";
    public const string Validating = "validating";
    public const string Validated = "validated";
    public const string Failed = "failed";
    public const string CommitQueued = "commit_queued";
    public const string Committing = "committing";
    public const string Committed = "committed";
    public const string Discarded = "discarded";
}

public static class ImportRowStatus
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Applied = "applied";
    public const string Failed = "failed";
}

public static class ImportActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
}

/// <summary>
/// One uploaded file and where it has got to: uploaded, validated (rows staged and checked), then committed.
/// Counts are in rows. A file-level problem (missing column, not UTF-8) fails the whole batch with FileErrors.
/// </summary>
public sealed class ImportBatch : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Kind { get; set; } = "parcels";
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FileSha256 { get; set; } = string.Empty;
    public string Status { get; set; } = ImportStatus.Uploaded;

    /// <summary>The file's column names, lower-case, in order. Staged rows hold values in the same order.</summary>
    public List<string> Header { get; set; } = [];
    public List<string> FileErrors { get; set; } = [];
    public List<string> FileWarnings { get; set; } = [];
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Failed { get; set; }
    public string? ValidateJobId { get; set; }
    public string? CommitJobId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public DateTime? CommittedAt { get; set; }
}

/// <summary>A slice of the uploaded file (about a megabyte), so a large upload never runs into the document size limit.</summary>
public sealed class ImportChunk : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public byte[] Data { get; set; } = [];
}

/// <summary>One staged row. RowNumber is the line in the uploaded file, so an error report points at the right row of the spreadsheet.</summary>
public sealed class ImportRow : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public List<string> Values { get; set; } = [];
    public string Status { get; set; } = ImportRowStatus.Valid;
    public List<ImportRowError> Errors { get; set; } = [];
    public string? Action { get; set; }
}

public sealed record ImportJobPayload(string BatchId);
