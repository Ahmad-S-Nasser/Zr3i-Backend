namespace Zr3i.Kernel.Identity;

/// <summary>RFC 4648 base32 without padding, the encoding authenticator apps use for TOTP secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var chars = new List<char>((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Add(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) chars.Add(Alphabet[(buffer << (5 - bits)) & 31]);
        return new string([.. chars]);
    }

    /// <exception cref="FormatException">The text has a character outside the base32 alphabet.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is '=' or ' ' or '-') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0) throw new FormatException("Not a base32 character.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
