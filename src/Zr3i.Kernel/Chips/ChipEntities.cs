using System.Text.RegularExpressions;
using Zr3i.Data;

namespace Zr3i.Kernel.Chips;

public static class ChipKinds
{
    public const string TrueColour = "true_colour";
    public const string Ndvi = "ndvi";

    public static readonly IReadOnlyList<string> All = [TrueColour, Ndvi];
}

/// <summary>
/// A small picture cut from one satellite scene around one parcel, as evidence for a person to look at. The picture is a file; this row is its
/// reference and what is needed to check and explain it. Append-only (rule 5): a picture is never edited, and a new boundary or scene gives a new one.
/// </summary>
public sealed class ParcelChip : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public string BoundaryId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>Where the file is, relative to the chip store's root. Built by the store from sanitised ids, never from a request.</summary>
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }

    /// <summary>How many picture pixels one 10 m satellite pixel is drawn as.</summary>
    public int Scale { get; set; }

    /// <summary>The 10 m lattice column and row of the picture's lower-left corner (the pixel test's lattice).</summary>
    public int OriginCol { get; set; }
    public int OriginRow { get; set; }
    public DateTime SensingTime { get; set; }
    public string SceneItemSha256 { get; set; } = string.Empty;
    public string PixelStackSha256 { get; set; } = string.Empty;
    public string PixelMethodId { get; set; } = string.Empty;
    public string Reader { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed record ChipPayload(string ParcelId, string SceneId);

/// <summary>Where picture files live. The web app reads them and the worker writes them, so both must point at the same place.</summary>
public interface IChipStore
{
    /// <summary>Writes a file (replacing a file with the same path, which holds the same bytes) and returns its path relative to the root.</summary>
    Task<string> WriteAsync(string tenantId, string parcelId, string sceneId, string kind, byte[] data, string sha256, CancellationToken ct = default);

    /// <summary>The file's bytes, or null if it is missing. Throws if the path would leave the root.</summary>
    Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>
/// Files under one root folder, one sub-folder per organisation: <c>{root}/{tenant}/{parcel}/{scene}-{kind}-{hash8}.png</c>. Every path part is
/// cleaned to letters, digits, dash and underscore, and a path read back is checked to stay inside the root, so no request can name a file.
/// </summary>
public sealed partial class FileChipStore(string root) : IChipStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task<string> WriteAsync(string tenantId, string parcelId, string sceneId, string kind, byte[] data, string sha256, CancellationToken ct = default)
    {
        var relative = Path.Combine(Clean(tenantId), Clean(parcelId), $"{Clean(sceneId)}-{Clean(kind)}-{Clean(sha256)[..Math.Min(8, sha256.Length)]}.png");
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data, ct);
        File.Move(temp, full, overwrite: true);
        return relative.Replace('\\', '/');
    }

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : null;
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The path leaves the chip store.");
        return full;
    }

    internal static string Clean(string part) => string.IsNullOrWhiteSpace(part) ? "_" : Unsafe().Replace(part, "_");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();
}
