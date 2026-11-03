namespace Zr3i.Data;

/// <summary>One page of a cursor-paginated read. NextCursor is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
