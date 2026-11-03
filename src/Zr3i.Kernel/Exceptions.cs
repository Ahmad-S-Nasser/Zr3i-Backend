namespace Zr3i.Kernel;

/// <summary>Input was refused. <see cref="Codes"/> are stable machine codes (the screens and API translate them), never prose.</summary>
public sealed class ValidationFailedException(IReadOnlyList<string> codes)
    : Exception("Validation failed: " + string.Join(", ", codes))
{
    public ValidationFailedException(string code) : this([code]) { }

    public IReadOnlyList<string> Codes { get; } = codes;
}

/// <summary>The row does not exist in this tenant. A row that exists in another tenant looks exactly the same.</summary>
public sealed class NotFoundException(string kind, string id) : Exception($"{kind} '{id}' was not found.")
{
    public string Kind { get; } = kind;
}
