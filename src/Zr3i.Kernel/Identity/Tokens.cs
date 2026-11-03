using System.Security.Cryptography;
using System.Text;

namespace Zr3i.Kernel.Identity;

/// <summary>Opaque random secrets. Only their SHA-256 is stored, so a database leak does not leak usable tokens.</summary>
internal static class Tokens
{
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashMatches(string storedHash, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(secret)));

    /// <summary>Tokens look like "{id}.{secret}"; ids never contain a dot in the secret part, so split on the last one.</summary>
    public static bool TrySplit(string? token, out string id, out string secret)
    {
        id = secret = string.Empty;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;
        id = token[..dot];
        secret = token[(dot + 1)..];
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
