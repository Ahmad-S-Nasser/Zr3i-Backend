using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zr3i.Data;

namespace Zr3i.Kernel.Methods;

/// <summary>
/// How much weight a parameter can bear. Nothing is allowed to look more settled than it is.
/// Derived: reproducible from the inputs by arithmetic. Specified: fixed in a formally agreed document or approved at freeze.
/// Starting rule: documented and in use, but provisional ("tuned by the hit rate"). Undecided: no value; needs an explicit decision.
/// </summary>
public static class ParameterStatus
{
    public const string Derived = "derived";
    public const string Specified = "specified";
    public const string StartingRule = "starting_rule";
    public const string Undecided = "undecided";

    public static readonly IReadOnlyList<string> All = [Derived, Specified, StartingRule, Undecided];
}

public static class MethodState
{
    public const string Draft = "draft";
    public const string Frozen = "frozen";
}

public sealed class MethodParameter
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Unit { get; set; }
    public string Status { get; set; } = ParameterStatus.Undecided;

    /// <summary>Where the value comes from: a document and section, or an owner decision with its date.</summary>
    public string Source { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>
/// One version of a method (for example ZR3I-R0-2026-01). Methods are platform-level, so rows live in the platform tenant. A draft can be
/// edited; a frozen version cannot, and carries a SHA-256 seal over its parameters so anyone can check it was not altered. A change is
/// always a new version.
/// </summary>
public sealed class MethodVersion : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string MethodId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string State { get; set; } = MethodState.Draft;
    public List<MethodParameter> Parameters { get; set; } = [];
    public string? Supersedes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? FrozenAt { get; set; }
    public string? FrozenBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ParametersSha256 { get; set; }

    public bool IsFrozen => State == MethodState.Frozen;

    public string? Get(string key) => Parameters.FirstOrDefault(p => p.Key == key)?.Value;

    public double GetDouble(string key) =>
        double.Parse(Get(key) ?? throw new InvalidOperationException($"Parameter '{key}' has no value."), CultureInfo.InvariantCulture);

    /// <summary>The seal: a hash over the method id, version and every parameter (key, value, unit, status, source), in key order.</summary>
    public string ComputeSeal()
    {
        var lines = Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}|{p.Value}|{p.Unit}|{p.Status}|{p.Source}");
        var text = $"{MethodId}|{Version}\n" + string.Join('\n', lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
