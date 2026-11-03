namespace Zr3i.Data;

/// <summary>A write broke a unique index (for example a second row with the same external id). Callers never see driver exceptions.</summary>
public sealed class DuplicateKeyException(Exception inner)
    : InvalidOperationException("A row with the same unique key already exists.", inner);
