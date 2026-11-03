using System.Security.Cryptography;

namespace Zr3i.Kernel.Identity;

/// <summary>
/// PBKDF2-SHA256 from the BCL. The stored value is "v1.{iterations}.{salt}.{hash}", so the cost can be raised
/// later while old hashes still verify.
/// </summary>
public sealed class PasswordHasher(int iterations = PasswordHasher.DefaultIterations)
{
    public const int DefaultIterations = 600_000;
    public const int MinimumLength = 12;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool MeetsPolicy(string? password) => password is { Length: >= MinimumLength };

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>False for a wrong password and for any malformed stored value; never throws on bad stored data.</summary>
    public bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        var parts = stored?.Split('.');
        if (parts is not { Length: 4 } || parts[0] != "v1") return false;
        if (!int.TryParse(parts[1], out var cost) || cost is < 1 or > 10_000_000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, cost, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
