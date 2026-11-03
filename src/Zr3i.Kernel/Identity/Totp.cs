using System.Security.Cryptography;

namespace Zr3i.Kernel.Identity;

/// <summary>RFC 6238 time-based one-time codes: HMAC-SHA1, 30-second steps, six digits, as authenticator apps expect.</summary>
public static class Totp
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private const int SecretBytes = 20;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public static string CodeAt(string secretBase32, DateTimeOffset at) =>
        CodeForStep(Base32.Decode(secretBase32), at.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>True if the code is valid now or one step either side (clock drift). Never throws on bad input.</summary>
    public static bool Verify(string secretBase32, string? code, DateTimeOffset now)
    {
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit)) return false;

        byte[] key;
        try { key = Base32.Decode(secretBase32); }
        catch (FormatException) { return false; }
        if (key.Length == 0) return false;

        var step = now.ToUnixTimeSeconds() / StepSeconds;
        var supplied = System.Text.Encoding.ASCII.GetBytes(code);
        var ok = false;
        for (var delta = -1; delta <= 1; delta++)
        {
            var expected = System.Text.Encoding.ASCII.GetBytes(CodeForStep(key, step + delta));
            ok |= CryptographicOperations.FixedTimeEquals(supplied, expected);
        }

        return ok;
    }

    private static string CodeForStep(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
